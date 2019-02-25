﻿using NiceHashMiner.Configs;
using NiceHashMiner.Devices.OpenCL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NiceHashMiner.Devices.Querying
{
    public class AmdQuery
    {
        private const string Tag = "AmdQuery";

        private readonly Dictionary<string, bool> _driverOld = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _noNeoscryptLyra2 = new Dictionary<string, bool>();

        private int _numDevs;

        public AmdQuery(int numDevs)
        {
            _numDevs = numDevs;
        }

        public List<OpenCLDevice> QueryAmd(bool openCLSuccess, OpenCLDeviceDetectionResult openCLData, out bool failedDriverCheck)
        {
            Helpers.ConsolePrint(Tag, "QueryAMD START");

            failedDriverCheck = DriverCheck();

            var amdDevices = openCLSuccess ? ProcessDevices(openCLData) : new List<OpenCLDevice>();

            Helpers.ConsolePrint(Tag, "QueryAMD END");

            return amdDevices;
        }

        private bool DriverCheck()
        {
            // check the driver version bool EnableOptimizedVersion = true;
            var showWarningDialog = false;
            var sgminerNoNeoscryptLyra2RE = new Version("21.19.164.1");

            foreach (var vidContrllr in SystemSpecs.AvailableVideoControllers)
            {
                if (!vidContrllr.IsAmd) continue;

                Helpers.ConsolePrint(Tag,
                    $"Checking AMD device (driver): {vidContrllr.Name} ({vidContrllr.DriverVersion})");

                _driverOld[vidContrllr.Name] = false;
                _noNeoscryptLyra2[vidContrllr.Name] = false;
                
                var amdDriverVersion = new Version(vidContrllr.DriverVersion);

                if (!ConfigManager.GeneralConfig.ForceSkipAMDNeoscryptLyraCheck &&
                    amdDriverVersion >= sgminerNoNeoscryptLyra2RE)
                {
                    _noNeoscryptLyra2[vidContrllr.Name] = true;
                    Helpers.ConsolePrint(Tag,
                        "Driver version seems to be " + sgminerNoNeoscryptLyra2RE +
                        " or higher. NeoScrypt and Lyra2REv2 will be removed from list");
                }

                if (amdDriverVersion.Major >= 15) continue;

                showWarningDialog = true;
                _driverOld[vidContrllr.Name] = true;
            }

            if (showWarningDialog)
            {
                Helpers.ConsolePrint(Tag,
                    "WARNING!!! Old AMD GPU driver detected! All optimized versions disabled, mining " +
                    "speed will not be optimal. Consider upgrading AMD GPU driver. Recommended AMD GPU driver version is 15.7.1.");
            }

            return showWarningDialog;
        }

        private List<OpenCLDevice> ProcessDevices(OpenCLDeviceDetectionResult openCLData)
        {
            var amdOclDevices = new List<OpenCLDevice>();
            var amdDevices = new List<OpenCLDevice>();

            var amdPlatformNumFound = false;
            foreach (var oclEl in openCLData.Platforms)
            {
                if (!oclEl.PlatformName.Contains("AMD") && !oclEl.PlatformName.Contains("amd")) continue;
                amdPlatformNumFound = true;
                var amdOpenCLPlatformStringKey = oclEl.PlatformName;
                AvailableDevices.AmdOpenCLPlatformNum = oclEl.PlatformNum;
                amdOclDevices = oclEl.Devices;
                Helpers.ConsolePrint(Tag,
                    $"AMD platform found: Key: {amdOpenCLPlatformStringKey}, Num: {AvailableDevices.AmdOpenCLPlatformNum}");
                break;
            }

            if (!amdPlatformNumFound) return amdDevices;

            // get only AMD gpus
            {
                foreach (var oclDev in amdOclDevices)
                {
                    if (oclDev._CL_DEVICE_TYPE.Contains("GPU"))
                    {
                        amdDevices.Add(oclDev);
                    }
                }
            }

            if (amdDevices.Count == 0)
            {
                Helpers.ConsolePrint(Tag, "AMD GPUs count is 0");
                return amdDevices;
            }

            Helpers.ConsolePrint(Tag, "AMD GPUs count : " + amdDevices.Count);
            Helpers.ConsolePrint(Tag, "AMD Getting device name and serial from ADL");
            // ADL
            var isAdlInit = QueryAdl.TryQuery(out var busIdInfos, out var numDevs);

            var isBusIDOk = true;
            // check if buss ids are unique and different from -1
            {
                var busIDs = new HashSet<int>();
                // Override AMD bus IDs
                var overrides = ConfigManager.GeneralConfig.OverrideAMDBusIds.Split(',');
                for (var i = 0; i < amdDevices.Count; i++)
                {
                    var amdOclDev = amdDevices[i];
                    if (overrides.Count() > i &&
                        int.TryParse(overrides[i], out var overrideBus) &&
                        overrideBus >= 0)
                    {
                        amdOclDev.AMD_BUS_ID = overrideBus;
                    }

                    if (amdOclDev.AMD_BUS_ID < 0 || !busIdInfos.ContainsKey(amdOclDev.AMD_BUS_ID))
                    {
                        isBusIDOk = false;
                        break;
                    }

                    busIDs.Add(amdOclDev.AMD_BUS_ID);
                }

                // check if unique
                isBusIDOk = isBusIDOk && busIDs.Count == amdDevices.Count;
            }
            // print BUS id status
            Helpers.ConsolePrint(Tag,
                isBusIDOk
                    ? "AMD Bus IDs are unique and valid. OK"
                    : "AMD Bus IDs IS INVALID. Using fallback AMD detection mode");

            ///////
            // AMD device creation (in NHM context)
            if (isAdlInit && isBusIDOk)
            {
                return AmdDeviceCreationPrimary(amdDevices, busIdInfos, numDevs);
            }

            return AmdDeviceCreationFallback(amdDevices);
        }

        private List<OpenCLDevice> AmdDeviceCreationPrimary(List<OpenCLDevice> amdDevices, 
            IReadOnlyDictionary<int, QueryAdl.BusIdInfo> busIdInfos, 
            int numDevs)
        {
            Helpers.ConsolePrint(Tag, "Using AMD device creation DEFAULT Reliable mappings");
            Helpers.ConsolePrint(Tag,
                amdDevices.Count == numDevs
                    ? "AMD OpenCL and ADL AMD query COUNTS GOOD/SAME"
                    : "AMD OpenCL and ADL AMD query COUNTS DIFFERENT/BAD");
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("");
            stringBuilder.AppendLine("QueryAMD [DEFAULT query] devices: ");
            foreach (var dev in amdDevices)
            {
                var busID = dev.AMD_BUS_ID;
                if (busID != -1 && busIdInfos.ContainsKey(busID))
                {
                    var deviceName = busIdInfos[busID].Name;
                    var newAmdDev = new AmdGpuDevice(dev, _driverOld[deviceName],
                        busIdInfos[busID].InfSection, _noNeoscryptLyra2[deviceName])
                    {
                        DeviceName = deviceName,
                        UUID = busIdInfos[busID].Uuid,
                        AdapterIndex = busIdInfos[busID].Adl1Index
                    };
                    var isDisabledGroup = ConfigManager.GeneralConfig.DeviceDetection
                        .DisableDetectionAMD;
                    var skipOrAdd = isDisabledGroup ? "SKIPED" : "ADDED";
                    var isDisabledGroupStr = isDisabledGroup ? " (AMD group disabled)" : "";
                    var etherumCapableStr = newAmdDev.IsEtherumCapable() ? "YES" : "NO";

                    AvailableDevices.AddDevice(
                        new AmdComputeDevice(newAmdDev, ++_numDevs, false,
                            busIdInfos[busID].Adl2Index));
                    // just in case 
                    try
                    {
                        stringBuilder.AppendLine($"\t{skipOrAdd} device{isDisabledGroupStr}:");
                        stringBuilder.AppendLine($"\t\tID: {newAmdDev.DeviceID}");
                        stringBuilder.AppendLine($"\t\tNAME: {newAmdDev.DeviceName}");
                        stringBuilder.AppendLine($"\t\tCODE_NAME: {newAmdDev.Codename}");
                        stringBuilder.AppendLine($"\t\tUUID: {newAmdDev.UUID}");
                        stringBuilder.AppendLine(
                            $"\t\tMEMORY: {newAmdDev.DeviceGlobalMemory}");
                        stringBuilder.AppendLine($"\t\tETHEREUM: {etherumCapableStr}");
                    }
                    catch
                    {
                    }
                }
                else
                {
                    stringBuilder.AppendLine($"\tDevice not added, Bus No. {busID} not found:");
                }
            }

            Helpers.ConsolePrint(Tag, stringBuilder.ToString());

            return amdDevices;
        }

        private List<OpenCLDevice> AmdDeviceCreationFallback(List<OpenCLDevice> amdDevices)
        {
            Helpers.ConsolePrint(Tag, "Using AMD device creation FALLBACK UnReliable mappings");
            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine("");
            stringBuilder.AppendLine("QueryAMD [FALLBACK query] devices: ");

            // get video AMD controllers and sort them by RAM
            // (find a way to get PCI BUS Numbers from PNPDeviceID)
            var amdVideoControllers = SystemSpecs.AvailableVideoControllers.Where(vcd => vcd.IsAmd).ToList();
            // sort by ram not ideal 
            amdVideoControllers.Sort((a, b) => (int) (a.AdapterRam - b.AdapterRam));
            amdDevices.Sort((a, b) =>
                (int) (a._CL_DEVICE_GLOBAL_MEM_SIZE - b._CL_DEVICE_GLOBAL_MEM_SIZE));
            var minCount = Math.Min(amdVideoControllers.Count, amdDevices.Count);

            for (var i = 0; i < minCount; ++i)
            {
                var deviceName = amdVideoControllers[i].Name;
                amdVideoControllers[i].SetInfSectionEmptyIfNull();
                var newAmdDev = new AmdGpuDevice(amdDevices[i], _driverOld[deviceName],
                    amdVideoControllers[i].InfSection,
                    _noNeoscryptLyra2[deviceName])
                {
                    DeviceName = deviceName,
                    UUID = "UNUSED"
                };
                var isDisabledGroup = ConfigManager.GeneralConfig.DeviceDetection
                    .DisableDetectionAMD;
                var skipOrAdd = isDisabledGroup ? "SKIPED" : "ADDED";
                var isDisabledGroupStr = isDisabledGroup ? " (AMD group disabled)" : "";
                var etherumCapableStr = newAmdDev.IsEtherumCapable() ? "YES" : "NO";

                AvailableDevices.AddDevice(
                    new AmdComputeDevice(newAmdDev, ++_numDevs, true, -1));
                // just in case 
                try
                {
                    stringBuilder.AppendLine($"\t{skipOrAdd} device{isDisabledGroupStr}:");
                    stringBuilder.AppendLine($"\t\tID: {newAmdDev.DeviceID}");
                    stringBuilder.AppendLine($"\t\tNAME: {newAmdDev.DeviceName}");
                    stringBuilder.AppendLine($"\t\tCODE_NAME: {newAmdDev.Codename}");
                    stringBuilder.AppendLine($"\t\tUUID: {newAmdDev.UUID}");
                    stringBuilder.AppendLine(
                        $"\t\tMEMORY: {newAmdDev.DeviceGlobalMemory}");
                    stringBuilder.AppendLine($"\t\tETHEREUM: {etherumCapableStr}");
                }
                catch
                {
                }
            }

            Helpers.ConsolePrint(Tag, stringBuilder.ToString());

            return amdDevices;
        }
    }
}
