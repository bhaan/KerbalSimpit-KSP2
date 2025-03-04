using BepInEx;
using HarmonyLib;
using JetBrains.Annotations;
using SpaceWarp;
using SpaceWarp.API.Mods;
using UnityEngine;
using KSP.Game;
using KerbalSimpit.Serial;
using System.Reflection;
using System.Runtime.InteropServices;
using Simpit.Providers;
using SpaceWarp.API.Logging;
using Simpit.External;


//TODO Support multiple serial ports
//TODO Automatically open port on game start
//TODO Add a Science Collection feature + info when science gets available 
//TODO CameraControl

//TODO Why are the EventData now called EventDataObsolete? Possible solution: Replace EventDataObsolete and GameEvents.XYZ with Messages and MessageCenter

//TODO AxisControl.cs : Test the outbound messages. The CommandProviders probably have to be added somewhere?
//TODO Is VesselChangedMessage the correct message to fire controlledVesselChangeEvent.Fire(OutboundPackets.VesselChange, VesselChangeValues.switching) ? In KSP 1 it fired on GameEvents.onVesselSwitching but there is no VesselSwitchedMessage in KSP2
//TODO FlightProvider.cs: For FlightStatusBits.isInFlight was HighLogic.LoadedSceneIsFlight used which is deprecated. Test if simVessel.IsVesselInFlight() also works
//TODO FlightProvider.cs: Does the crew count work correctly?
//TODO Check if the onFlightReady and the onGameSceneSwitchRequested events are fired correctly.
//TODO Telemetry.cs : Please check/test especially airspeed, maneuverData, rotationData 
//TODO Resources.cs : Test Ablator per Stage. It might not work because the per stage calculation only looks at fuel
//TODO TargetInfo.cs: Test the TargetProvider
//TODO Does the scene change notification stuff work?
//TODO FlightProvider.cs: Get a better CommNet signal strength. Is there something like that in KSP2? Can it be calculated by antennas and distance?

//TODO WarpControl.cs : Timewarp to PE goes past the Pe if Pe is in another SOI (e.g. going to the mun)
//TODO WarpControl.cs : warp levels are different between KSP2 and KSP1 how to handle that?
//TODO WarpControl.cs : timewarp to Next morining doesn't work

//TODO Arduino lib - Resources.cs : Implement other resources: OutboundPackets 52 to 62
//TODO Add RadiatorPanels Action group. This is the ninth Action group so all the action group messages need a second byte of payload
//TODO For the Action groups there is now the state "mixed", e.g. if only some lights are on, others are off. Should the mixed state be counted as on or off? Or is there a possibility to also send the mixed state?
//TODO FlightProvider.cs: what about the FlightStatusBits.isInAtmoTW? I currently have it sending tw.IsPhysicsTimeWarp
//TODO FlightProvider.cs: There is the simVessel.ControlStatus (which is a VesselControlState, it has NoControl, NoCommNet, FullControlHibernation, FullControl) and there is simVessel._commandControlState (which is a CommandControlState , it has Disabled, NothEnoughCrew, NotEnoughResources, NoCommnetConnection, Hibernating, FullyFunctional). Which one should we use?
//TODO FlightProvider.cs: the vesselType (debris, rover, probe, ship, ...) does not seem to exist in KSP2 the closest is the MapItemType.
//TODO FlightProvider.cs: Test the currentStage. It sends -1

//TODO Two more new messages for action groups: SetSingleActionGroup and FeedbackValue (On, Off, Mixed) on all three sides (Arduino, KSP1, KSP2)
//Extend define of action groups: 6 bits for addr of action group  2 bits for state on,off,mixed,notAvailable
//                                                                 2 bits for on,off,toggle
//Wrapper functions for arduino

//TODO Work on an Arduino side that can test all the features. Should come in handy when I have to update the KSP2 side. Could also come in handy as an example to show how to use all the functions.

namespace Simpit;

public delegate void ToDeviceCallback();

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency(SpaceWarpPlugin.ModGuid, SpaceWarpPlugin.ModVer)]
public class SimpitPlugin : BaseSpaceWarpPlugin
{
    // Useful in case some other mod wants to use this mod a dependency
    [PublicAPI] public const string ModGuid = MyPluginInfo.PLUGIN_GUID;
    [PublicAPI] public const string ModName = MyPluginInfo.PLUGIN_NAME;
    [PublicAPI] public const string ModVer = MyPluginInfo.PLUGIN_VERSION;

    // Singleton instance of the plugin class
    [PublicAPI] public static SimpitPlugin Instance { get; set; }
    internal new SpaceWarp.API.Logging.ILogger Logger = new UnityLogSource(ModName);
    SimpitGui gui = new SimpitGui();

    public bool config_verbose;
    int config_refreshRate;

    //Serial Port
    public string config_SerialPortName;
    public int config_SerialPortBaudRate;
    public KSPSerialPort port;

    //Serial Data Management
    // To receive events from serial devices on channel i,
    // register a callback for onSerialReceivedArray[i].
    public EventDataObsolete<byte, object>[] onSerialReceivedArray = new EventDataObsolete<byte, object>[256];
    // To send a packet on channel i, call
    // toSerialArray[i].Fire()
    public EventDataObsolete<byte, object>[] toSerialArray = new EventDataObsolete<byte, object>[256];
    // To be notified when a message must be sent (to send a first
    // non-periodic message when a channel is subscribed for instance),
    // register a callback for onSerialChannelForceSendArray[i].
    public EventDataObsolete<byte, object>[] onSerialChannelForceSendArray = new EventDataObsolete<byte, object>[256];

    GameObject providers;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    [Serializable]
    public struct HandshakePacket
    {
        public byte HandShakeType;
        public byte Payload;
    }

    private static List<ToDeviceCallback> RegularEventList =
            new List<ToDeviceCallback>(255);
    private bool DoEventDispatching = false;
    private Thread EventDispatchThread;



    /// <summary>
    /// Runs when the mod is first initialized.
    /// </summary>
    public override void OnInitialized()
    {
        base.OnInitialized();

        Assembly ass = Assembly.LoadFile(Path.Combine(PluginFolderPath, "assets", "lib", "System.ComponentModel.Primitives.dll"));
        Logger.LogDebug("Loaded dll: " + ass.ToString());
        ass = Assembly.LoadFile(Path.Combine(PluginFolderPath, "assets", "lib", "System.IO.Ports.dll"));
        Logger.LogDebug("Loaded dll: " + ass.ToString());

        Instance = this;

        ReadConfig();

        // Log the config value into <KSP2 Root>/BepInEx/LogOutput.log
        Logger.LogInfo($"Using Serial Port \"{config_SerialPortName}\" with Baud Rate \"{config_SerialPortBaudRate}\"");

        //Initialize everything needed for Serial
        InitSerial();

        //Initialize everything needed for the Providers
        InitProviders();

        gui.InitGui();
        
        // Register all Harmony patches in the project
        Harmony.CreateAndPatchAll(typeof(SimpitPlugin).Assembly);
    }

    public void ReadConfig()
    {
        // Fetch configuration values or create a default one if it does not exist
        const string defaultComPort = "COMxx";
        var comPortValue = Config.Bind<string>("Settings section", "Serial Port Name", defaultComPort, "Which Serial Port the controller uses. E.g. COM4");
        config_SerialPortName = comPortValue.Value;

        const int defaultBaudRate = 115200;
        var baudRateValue = Config.Bind<int>("Settings section", "Baud Rate", defaultBaudRate, "Which speed the Serial Port uses. E.g. 115200");
        config_SerialPortBaudRate = baudRateValue.Value;

        const bool defaultVerbose = false;
        var verboseValue = Config.Bind<bool>("Settings section", "Verbose Mode", defaultVerbose, "Should verbose logs be generated");
        config_verbose = verboseValue.Value;

        const int defaultRefreshRate = 125;
        var refreshRateValue = Config.Bind<int>("Settings section", "Refresh Rate", defaultRefreshRate, "Refresh rate in milliseconds. E.g. 125");
        config_refreshRate = refreshRateValue.Value;

        if(port != null) 
        { 
            if (port.PortName != config_SerialPortName || port.BaudRate != config_SerialPortBaudRate) port.ChangePort(config_SerialPortName, config_SerialPortBaudRate); 
        }
    }
    private void OnGUI()
    {
        gui.OnGui();
    }

    public void OpenPort()
    {
        if (port.portStatus != KSPSerialPort.ConnectionStatus.CLOSED && port.portStatus != KSPSerialPort.ConnectionStatus.ERROR)
        {
            //Port already opened. Nothing to do.
            Logger.LogInfo(String.Format("Port {0} already opened. Nothing to do.", port.PortName));

            GameManager.Instance.Game.Notifications.ProcessNotification(new NotificationData
            {
                Tier = NotificationTier.Passive,
                Primary = new NotificationLineItemData { LocKey = String.Format("Simpit: Port {0} already opened.Nothing to do.", port.PortName) }
            });
            return;
        }

        String portName = port.PortName;
        if (portName.StartsWith("COM") || portName.StartsWith("/"))
        {
            if(portName.Equals("COMxx"))
            {
                Logger.LogWarning("port name is default for port " + port.ID + ". Please provide a specific port the Simpit configs in the main menu.");
                GameManager.Instance.Game.Notifications.ProcessNotification(new NotificationData
                {
                    Tier = NotificationTier.Passive,
                    Primary = new NotificationLineItemData { LocKey = "Simpit: No Serial Port defined. Go to config (main menu -> mods) to set one." }
                });
                return;
            }
        }
        else
        {
            Logger.LogWarning("no port name is defined for port " + port.ID + ". Please check the Simpit configs in the main menu.");
            return;
        }

        if (port.open())
        {
            Logger.LogInfo(String.Format("Opened port {0}", portName));

            GameManager.Instance.Game.Notifications.ProcessNotification(new NotificationData
            {
                Tier = NotificationTier.Passive,
                Primary = new NotificationLineItemData { LocKey = String.Format("Simpit: Opened port {0}", portName) }
            });
        }
        else
        {
            Logger.LogInfo(String.Format("Unable to open port {0}", portName));

            GameManager.Instance.Game.Notifications.ProcessNotification(new NotificationData
            {
                Tier = NotificationTier.Passive,
                Primary = new NotificationLineItemData { LocKey = String.Format("Simpit: Unable to open port {0}", portName) }
            });
        }

        if (!DoEventDispatching)
            StartEventDispatch();
    }

    public void ClosePort()
    {
        if (port == null) return;

        port.close();
    }

    // Method that inits the ports. Will only be called once to initialize them when starting the mod.
    private void InitSerial()
    {
        //Create the serial port
        port = new KSPSerialPort(config_SerialPortName, config_SerialPortBaudRate);

        for (int i = 254; i >= 0; i--)
        {
            this.onSerialReceivedArray[i] = new EventDataObsolete<byte, object>(String.Format("onSerialReceived{0}", i));
            this.toSerialArray[i] = new EventDataObsolete<byte, object>(String.Format("toSerial{0}", i));
            this.onSerialChannelForceSendArray[i] = new EventDataObsolete<byte, object>(String.Format("onSerialChannelForceSend{0}", i));
        }

        this.onSerialReceivedArray[CommonPackets.Synchronisation].Add(this.handshakeCallback);
        this.onSerialReceivedArray[InboundPackets.CloseSerialPort].Add(this.serialCalledClose);
        this.onSerialReceivedArray[InboundPackets.RegisterHandler].Add(this.registerCallback);
        this.onSerialReceivedArray[InboundPackets.DeregisterHandler].Add(this.deregisterCallback);
        this.onSerialReceivedArray[InboundPackets.RequestMessage].Add(this.requestMessageCallback);
    }

    private void InitProviders()
    {
        providers = new GameObject();
        providers.AddComponent<KerbalSimpitEchoProvider>();
        providers.AddComponent<KerbalSimpitAxisController>();
        providers.AddComponent<KerbalSimpitActionProvider>();
        providers.AddComponent<KerbalSimpitTelemetryProvider>();
        providers.AddComponent<KerbalSimpitWarpControl>();
        providers.AddComponent<KerbalSimpitNavBallProvider>();
        providers.AddComponent<FlightStatusProvider>();
        providers.AddComponent<KerbalSimpitCAGProvider>();
        providers.AddComponent<KeyboardEmulator>();

        providers.AddComponent<MonoPropellantProvider>();
        providers.AddComponent<SolidFuelProvider>();
        providers.AddComponent<SolidFuelStageProvider>();
        providers.AddComponent<IntakeAirProvider>();
        providers.AddComponent<TestRocksProvider>();
        providers.AddComponent<EvaPropellantProvider>();
        providers.AddComponent<HydrogenProvider>();
        providers.AddComponent<HydrogenStageProvider>();
        providers.AddComponent<LiquidFuelProvider>();
        providers.AddComponent<LiquidFuelStageProvider>();
        providers.AddComponent<OxidizerProvider>();
        providers.AddComponent<OxidizerStageProvider>();
        providers.AddComponent<MethaloxProvider>();
        providers.AddComponent<MethaloxStageProvider>();
        providers.AddComponent<MethaneAirProvider>();
        providers.AddComponent<MethaneAirStageProvider>();
        providers.AddComponent<UraniumProvider>();
        providers.AddComponent<ElectricChargeProvider>();
        providers.AddComponent<XenonGasProvider>();
        providers.AddComponent<XenonGasStageProvider>();
        providers.AddComponent<XenonECProvider>();
        providers.AddComponent<XenonECStageProvider>();
        providers.AddComponent<AblatorProvider>();
        providers.AddComponent<AblatorStageProvider>();
        providers.AddComponent<KerbalSimpitTargetProvider>();
    }

    private void StartEventDispatch()
    {
        this.EventDispatchThread = new Thread(this.EventWorker);
        this.EventDispatchThread.Start();
        while (!this.EventDispatchThread.IsAlive) ;
    }

    public static void AddToDeviceHandler(ToDeviceCallback cb)
    {
        RegularEventList.Add(cb);
    }

    public static bool RemoveToDeviceHandler(ToDeviceCallback cb)
    {
        return RegularEventList.Remove(cb);
    }

    private void EventWorker()
    {
        Action EventNotifier = null;
        ToDeviceCallback[] EventListCopy = new ToDeviceCallback[255];
        int EventCount;
        int TimeSlice;
        EventNotifier = delegate {
            EventCount = RegularEventList.Count;
            RegularEventList.CopyTo(EventListCopy);
            if (EventCount > 0)
            {
                TimeSlice = config_refreshRate / EventCount;
                for (int i = EventCount; i >= 0; --i)
                {
                    if (EventListCopy[i] != null)
                    {
                        EventListCopy[i]();
                        Thread.Sleep(TimeSlice);
                    }
                }
            }
            else
            {
                Thread.Sleep(config_refreshRate);
            }
        };
        DoEventDispatching = true;
        Logger.LogInfo("Starting event dispatch loop");
        while (DoEventDispatching)
        {
            EventNotifier();
        }
        Logger.LogInfo("Event dispatch loop exiting");
    }

    private void handshakeCallback(byte portID, object data)
    {
        byte[] payload = (byte[])data;
        HandshakePacket hs;
        hs.Payload = HandshakeValues.KerbalSpaceProgram2;
        switch (payload[0])
        {
            case 0x00:
                if (config_verbose) Logger.LogInfo(String.Format("SYN received on port {0}. Replying.", port.PortName));

                //When handshake is started, unregister all channels to avoid duplication of messages when new channels are subscribed after an Arduino reset
                for (int idx = 0; idx < 255; idx++)
                {
                    toSerialArray[idx].Remove(port.sendPacket);
                }
                // Remove all messages not yet sent to make sure the next message sent is an SYNACK
                port.clearSendingQueue();

                port.portStatus = KSPSerialPort.ConnectionStatus.HANDSHAKE;
                hs.HandShakeType = 0x01;
                port.sendPacket(CommonPackets.Synchronisation, hs);
                break;
            case 0x01:
                if (config_verbose) Logger.LogInfo(String.Format("SYNACK received on port {0}. Replying.", port.PortName));
                port.portStatus = KSPSerialPort.ConnectionStatus.CONNECTED;
                hs.HandShakeType = 0x02;
                port.sendPacket(CommonPackets.Synchronisation, hs);
                break;
            case 0x02:
                byte[] verarray = new byte[payload.Length - 1];
                Array.Copy(payload, 1, verarray, 0,
                           (payload.Length - 1));
                string VersionString = System.Text.Encoding.UTF8.GetString(verarray);
                Logger.LogInfo(String.Format("ACK received on port {0}. Handshake complete, Resetting channels, Arduino library version '{1}'.", port.PortName, VersionString));
                port.removeAllPacketSubscriptionRecords();
                port.portStatus = KSPSerialPort.ConnectionStatus.CONNECTED;

                break;
        }
    }

    private void serialCalledClose(byte portID, object data)
    {
        // Spit out log that the port wants to be closed
        if (config_verbose)
        {
            Logger.LogInfo(String.Format("Serial port {0} asked to be closed", portID));
        }

        foreach (int packetID in port.getPacketSubscriptionList())
        {

            // Remove the callback of the serial port from the event caller
            toSerialArray[packetID].Remove(port.sendPacket);

            if (config_verbose)
            {
                Logger.LogInfo(String.Format("Serial port {0} unsubscribed from packet {1}", portID, packetID));
            }
        }

        ClosePort();
    }

    private void registerCallback(byte portID, object data)
    {
        byte[] payload = (byte[])data;
        byte idx;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            idx = payload[i];


            if (!port.isPacketSubscribedTo(idx))
            {
                if (config_verbose)
                {
                    Logger.LogInfo(String.Format("Serial port {0} subscribing to channel {1}", portID, idx));
                }
                // Adds the sendPacket method as a callback to the event that is called when a value in the toSerialArray is updated
                toSerialArray[idx].Add(port.sendPacket);
                onSerialChannelForceSendArray[idx].Fire(idx, null);
                // Adds a record of the port subscribing to a packet to a list stored in the port instance.
                port.addPacketSubscriptionRecord(idx);
            }
            else
            {
                if (config_verbose) Logger.LogInfo(String.Format("Serial port {0} trying to subscribe to channel {1} but is already subscribed. Ignoring it", portID, idx));
            }
        }
    }

    private void deregisterCallback(byte portID, object data)
    {
        byte[] payload = (byte[])data;
        byte idx;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            idx = payload[i];
            toSerialArray[idx].Remove(port.sendPacket);
            // Removes the record of a port subscribing to a packet from the port's internal record
            port.removePacketSubscriptionRecord(idx);
            if (config_verbose)
            {
                Logger.LogInfo(String.Format("Serial port {0} ubsubscribed from channel {1}", portID, idx));
            }
        }
    }

    private void requestMessageCallback(byte portID, object data)
    {
        byte[] payload = (byte[])data;
        byte channelID = payload[0];

        if (channelID == 0)
        {
            foreach (byte packetID in port.getPacketSubscriptionList())
            {
                onSerialChannelForceSendArray[packetID].Fire(packetID, null);
            }
        }
        else
        {
            onSerialChannelForceSendArray[channelID].Fire(channelID, null);
        }
    }
}

