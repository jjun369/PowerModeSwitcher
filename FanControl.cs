using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PowerModeSwitcher
{
    internal sealed class FanPresetRepository
    {
        private readonly string _path;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public FanPresetRepository(string path)
        {
            _path = path;
        }

        public FanPresetDocument Load()
        {
            if (!File.Exists(_path))
            {
                throw new FileNotFoundException("fan-presets.json 파일을 찾을 수 없습니다.", _path);
            }

            FanPresetDocument document = _serializer.Deserialize<FanPresetDocument>(File.ReadAllText(_path, Encoding.UTF8));
            FanPresetValidator.Validate(document);
            return document;
        }
    }

    internal static class FanPresetValidator
    {
        public static void Validate(FanPresetDocument document)
        {
            if (document == null || String.IsNullOrWhiteSpace(document.modelName) ||
                String.IsNullOrWhiteSpace(document.systemProductName) || String.IsNullOrWhiteSpace(document.baseBoardProduct) ||
                document.temperaturePoints == null ||
                document.presets == null || document.presets.Count == 0)
            {
                throw new InvalidDataException("fan-presets.json의 필수 항목이 없습니다.");
            }

            if (!String.Equals(document.systemProductName, FanHardwareGate.SystemProductName, StringComparison.Ordinal) ||
                !String.Equals(document.baseBoardProduct, FanHardwareGate.BaseBoardProduct, StringComparison.Ordinal))
            {
                throw new InvalidDataException("fan-presets.json의 하드웨어 식별값은 이 EXE가 지원하는 GP66 11UG 검증값과 일치해야 합니다.");
            }

            ValidateTemperatures(document.temperaturePoints, "공통 온도 포인트");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FanPreset preset in document.presets)
            {
                if (preset == null || String.IsNullOrWhiteSpace(preset.id) || String.IsNullOrWhiteSpace(preset.name))
                {
                    throw new InvalidDataException("팬 프리셋의 id와 이름은 비워 둘 수 없습니다.");
                }

                if (!ids.Add(preset.id))
                {
                    throw new InvalidDataException("중복된 팬 프리셋 id: " + preset.id);
                }

                ValidateSpeeds(preset.cpuSpeeds, "CPU", preset.id);
                ValidateSpeeds(preset.gpuSpeeds, "GPU", preset.id);
            }
        }

        public static void ValidateCurve(int[] temperatures, int[] cpuSpeeds, int[] gpuSpeeds)
        {
            ValidateHardwareTemperatures(temperatures, "현재 EC 팬 곡선");
            ValidateSpeeds(cpuSpeeds, "CPU", "사용자 곡선");
            ValidateSpeeds(gpuSpeeds, "GPU", "사용자 곡선");
        }

        private static void ValidateHardwareTemperatures(int[] values, string name)
        {
            if (values == null || values.Length != 6 || values[0] != 0 || values[5] < 50)
            {
                throw new InvalidDataException(name + " 온도 포인트를 읽을 수 없습니다.");
            }

            int index;
            for (index = 0; index < values.Length; index++)
            {
                if (values[index] < 0 || values[index] > 100 || (index > 0 && values[index] <= values[index - 1]))
                {
                    throw new InvalidDataException(name + " 온도 포인트가 올바르지 않습니다.");
                }
            }
        }

        private static void ValidateTemperatures(int[] values, string name)
        {
            if (values == null || values.Length != 6 || values[0] != 0 || values[5] < 85)
            {
                throw new InvalidDataException(name + "은 0°C부터 시작하는 6개 포인트이며 마지막 온도는 85°C 이상이어야 합니다.");
            }

            int index;
            for (index = 0; index < values.Length; index++)
            {
                if (values[index] < 0 || values[index] > 100 || (index > 0 && values[index] <= values[index - 1]))
                {
                    throw new InvalidDataException(name + " 온도 값은 0~100 범위에서 오름차순이어야 합니다.");
                }
            }
        }

        private static void ValidateSpeeds(int[] values, string fanName, string presetId)
        {
            if (values == null || values.Length != 6 || values[0] != 0 || values[5] != 100)
            {
                throw new InvalidDataException(presetId + "의 " + fanName + " 팬 속도는 0%로 시작해 100%로 끝나는 6개 포인트여야 합니다.");
            }

            int index;
            for (index = 0; index < values.Length; index++)
            {
                if (values[index] < 0 || values[index] > 100 || (index > 0 && values[index] < values[index - 1]))
                {
                    throw new InvalidDataException(presetId + "의 " + fanName + " 팬 속도는 0~100 범위에서 감소하지 않아야 합니다.");
                }
            }
        }
    }

    internal static class FanHardwareGate
    {
        public const string SystemProductName = "GP66 Leopard 11UG";
        public const string BaseBoardProduct = "MS-1543";
    }

    internal sealed class FanPresetDocument
    {
        public string modelName { get; set; }
        public string systemProductName { get; set; }
        public string baseBoardProduct { get; set; }
        public int[] temperaturePoints { get; set; }
        public List<FanPreset> presets { get; set; }
    }

    internal sealed class FanPreset
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public int[] cpuSpeeds { get; set; }
        public int[] gpuSpeeds { get; set; }
    }

    internal sealed class FanHardwareStatus
    {
        public bool reachable { get; set; }
        public bool writeEnabled { get; set; }
        public string systemProductName { get; set; }
        public string baseBoardProduct { get; set; }
        public string firmware { get; set; }
        public string message { get; set; }
        public int fanMode { get; set; }
        public bool coolerBoost { get; set; }
        public int cpuTemperature { get; set; }
        public int gpuTemperature { get; set; }
        public int cpuDuty { get; set; }
        public int gpuDuty { get; set; }
        public int cpuRpm { get; set; }
        public int gpuRpm { get; set; }
        public int[] cpuTemperatures { get; set; }
        public int[] cpuSpeeds { get; set; }
        public int[] gpuTemperatures { get; set; }
        public int[] gpuSpeeds { get; set; }
    }

    internal sealed class FanState
    {
        public string lastAppliedPreset { get; set; }
        public string lastAppliedAt { get; set; }
        public FanBaselineState baseline { get; set; }
    }

    internal sealed class FanBaselineState
    {
        public string firmware { get; set; }
        public int fanMode { get; set; }
        public bool coolerBoost { get; set; }
        public int[] cpuTemperatures { get; set; }
        public int[] cpuSpeeds { get; set; }
        public int[] gpuTemperatures { get; set; }
        public int[] gpuSpeeds { get; set; }
    }

    internal sealed class FanActionResult
    {
        public bool success { get; set; }
        public string title { get; set; }
        public string message { get; set; }
        public FanHardwareStatus status { get; set; }

        public static FanActionResult Success(string title, string message, FanHardwareStatus status)
        {
            return new FanActionResult { success = true, title = title, message = message, status = status };
        }

        public static FanActionResult Failure(string title, string message, FanHardwareStatus status)
        {
            return new FanActionResult { success = false, title = title, message = message, status = status };
        }

        public string ToDisplayText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(message);
            if (status != null)
            {
                text.AppendLine();
                text.Append("펌웨어: ").AppendLine(String.IsNullOrWhiteSpace(status.firmware) ? "확인 불가" : status.firmware);
                text.Append("팬 모드: ").AppendLine(FanText.Mode(status.fanMode));
                text.Append("Cooler Boost: ").AppendLine(status.coolerBoost ? "ON" : "OFF");
                text.Append("CPU: ").Append(status.cpuTemperature).Append("°C / ").Append(status.cpuDuty).Append("% / ").Append(status.cpuRpm).AppendLine(" RPM");
                text.Append("GPU: ").Append(status.gpuTemperature).Append("°C / ").Append(status.gpuDuty).Append("% / ").Append(status.gpuRpm).Append(" RPM");
            }

            return text.ToString();
        }
    }

    internal static class FanText
    {
        public static string Mode(int value)
        {
            if (value == 0x0D) return "Auto / 기본";
            if (value == 0x1D) return "MSI Silent";
            if (value == 0x8D) return "Advanced 곡선";
            return "알 수 없음 (0x" + value.ToString("X2") + ")";
        }

        public static string Curve(int[] temperatures, int[] speeds)
        {
            if (temperatures == null || speeds == null || temperatures.Length != speeds.Length)
            {
                return "읽을 수 없음";
            }

            List<string> points = new List<string>();
            int index;
            for (index = 0; index < temperatures.Length; index++)
            {
                points.Add(temperatures[index] + "°C→" + speeds[index] + (speeds[index] <= 100 ? "%" : " (EC 원시값)"));
            }

            return String.Join(" · ", points.ToArray());
        }
    }

    internal sealed class FanService
    {
        private readonly StateRepository _stateRepository;
        private readonly FanPresetDocument _configuration;
        private readonly MsiFanWmiBackend _backend;
        private readonly object _operationLock = new object();

        public FanService(StateRepository stateRepository, FanPresetDocument configuration)
        {
            _stateRepository = stateRepository;
            _configuration = configuration;
            _backend = new MsiFanWmiBackend();
        }

        public FanActionResult Query()
        {
            FanHardwareStatus status = _backend.Query(_configuration);
            return status.reachable
                ? FanActionResult.Success("팬 상태", status.message, status)
                : FanActionResult.Failure("팬 상태", status.message, status);
        }

        public FanActionResult ApplyPreset(FanPreset preset)
        {
            if (preset == null)
            {
                return FanActionResult.Failure("팬 곡선", "선택된 팬 프리셋이 없습니다.", null);
            }

            lock (_operationLock)
            {
                try
                {
                    FanHardwareStatus before = RequireWritableStatus();
                    CaptureBaseline(before);
                    // MSI's EC treats the temperature columns as a fixed table layout.
                    // Keep the live model-specific points and replace only the requested
                    // speed columns; writing generic 0/50/60/70/80/90 points can be ignored.
                    FanHardwareStatus after = _backend.ApplyCurve(
                        _configuration,
                        Copy(before.cpuTemperatures),
                        preset.cpuSpeeds,
                        Copy(before.gpuTemperatures),
                        preset.gpuSpeeds);
                    Remember(preset.id);
                    return FanActionResult.Success("팬 곡선 적용", preset.name + "을(를) 적용했습니다. 현재 온도 포인트를 유지하고 속도 곡선만 교체했습니다. 팬 RPM은 1~3초 후 안정됩니다.", after);
                }
                catch (Exception exception)
                {
                    return FanActionResult.Failure("팬 곡선 적용 실패", exception.Message, SafeQuery());
                }
            }
        }

        public FanActionResult SetAuto()
        {
            lock (_operationLock)
            {
                try
                {
                    FanHardwareStatus before = RequireWritableStatus();
                    CaptureBaseline(before);
                    FanHardwareStatus after = _backend.SetAuto(_configuration);
                    Remember("auto");
                    return FanActionResult.Success("기본 팬 모드", "MSI Auto / 기본 팬 모드로 복원했습니다.", after);
                }
                catch (Exception exception)
                {
                    return FanActionResult.Failure("기본 팬 모드 실패", exception.Message, SafeQuery());
                }
            }
        }

        public FanActionResult SetCoolerBoost(bool enabled)
        {
            lock (_operationLock)
            {
                try
                {
                    FanHardwareStatus before = RequireWritableStatus();
                    CaptureBaseline(before);
                    FanHardwareStatus after = _backend.SetCoolerBoost(_configuration, enabled);
                    Remember(enabled ? "cooler-boost" : "cooler-boost-off");
                    return FanActionResult.Success(
                        enabled ? "Cooler Boost" : "Cooler Boost 해제",
                        enabled ? "Cooler Boost를 켰습니다. CPU와 GPU 팬을 최대 속도로 강제합니다." : "Cooler Boost를 해제했습니다.",
                        after);
                }
                catch (Exception exception)
                {
                    return FanActionResult.Failure("Cooler Boost 변경 실패", exception.Message, SafeQuery());
                }
            }
        }

        public FanActionResult RestoreBaseline()
        {
            lock (_operationLock)
            {
                try
                {
                    AppState state = _stateRepository.Load();
                    if (state.fan == null || state.fan.baseline == null)
                    {
                        return FanActionResult.Failure("팬 baseline 복원", "PowerModeSwitcher가 저장한 최초 팬 설정이 없습니다. OEM 값을 추정하지 않습니다.", SafeQuery());
                    }

                    FanHardwareStatus current = RequireWritableStatus();
                    FanHardwareStatus after = _backend.Restore(_configuration, state.fan.baseline);
                    Remember("restore");
                    return FanActionResult.Success("팬 baseline 복원", "PowerModeSwitcher가 저장한 최초 팬 설정을 복원했습니다.", after);
                }
                catch (Exception exception)
                {
                    return FanActionResult.Failure("팬 baseline 복원 실패", exception.Message, SafeQuery());
                }
            }
        }

        private FanHardwareStatus RequireWritableStatus()
        {
            FanHardwareStatus status = _backend.Query(_configuration);
            if (!status.writeEnabled)
            {
                throw new InvalidOperationException(status.message);
            }

            return status;
        }

        private void CaptureBaseline(FanHardwareStatus status)
        {
            AppState state = _stateRepository.Load();
            state.fan = state.fan ?? new FanState();
            if (state.fan.baseline != null)
            {
                return;
            }

            if (!HasCompleteCurve(status))
            {
                throw new InvalidOperationException("현재 팬 곡선을 완전히 읽지 못해 baseline을 저장하지 않았습니다.");
            }

            state.fan.baseline = new FanBaselineState
            {
                firmware = status.firmware,
                fanMode = status.fanMode,
                coolerBoost = status.coolerBoost,
                cpuTemperatures = Copy(status.cpuTemperatures),
                cpuSpeeds = Copy(status.cpuSpeeds),
                gpuTemperatures = Copy(status.gpuTemperatures),
                gpuSpeeds = Copy(status.gpuSpeeds)
            };
            _stateRepository.Save(state);
        }

        private void Remember(string presetId)
        {
            AppState state = _stateRepository.Load();
            state.fan = state.fan ?? new FanState();
            state.fan.lastAppliedPreset = presetId;
            state.fan.lastAppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _stateRepository.Save(state);
        }

        private FanHardwareStatus SafeQuery()
        {
            try
            {
                return _backend.Query(_configuration);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasCompleteCurve(FanHardwareStatus status)
        {
            return status != null && status.cpuTemperatures != null && status.cpuSpeeds != null &&
                status.gpuTemperatures != null && status.gpuSpeeds != null &&
                status.cpuTemperatures.Length == 6 && status.cpuSpeeds.Length == 6 &&
                status.gpuTemperatures.Length == 6 && status.gpuSpeeds.Length == 6;
        }

        private static int[] Copy(int[] values)
        {
            return values == null ? null : (int[])values.Clone();
        }
    }

    internal sealed class MsiSystemIdentity
    {
        public string systemProductName { get; set; }
        public string baseBoardProduct { get; set; }
    }

    // This backend intentionally uses only the MSI_ACPI WMI schema installed by MSI Center.
    // It is model- and firmware-locked; see THIRD_PARTY_NOTICES.md for GhostDeck attribution.
    internal sealed class MsiFanWmiBackend
    {
        private const string WmiNamespace = @"root\WMI";
        private const string WmiClass = "MSI_ACPI";
        private const string PackageClass = "Package_32";
        private const byte CpuTemperatureAddress = 0x68;
        private const byte GpuTemperatureAddress = 0x80;
        private const byte CpuDutyAddress = 0x71;
        private const byte GpuDutyAddress = 0x89;
        private const byte CpuRpmAddress = 0xC9;
        private const byte GpuRpmAddress = 0xCB;
        private const byte FanModeAddress = 0xD4;
        private const byte CoolerBoostAddress = 0x98;
        private const byte CoolerBoostMask = 0x80;
        private const byte AutoMode = 0x0D;
        private const byte AdvancedMode = 0x8D;
        private const int RpmDivisor = 478000;
        private const int MaxPlausibleRpm = 12000;

        private readonly object _sync = new object();
        private ManagementObject _instance;
        private ManagementClass _package;

        public FanHardwareStatus Query(FanPresetDocument configuration)
        {
            try
            {
                MsiSystemIdentity identity = ReadSystemIdentity();
                return WithSession(delegate(ManagementObject instance, ManagementClass package)
                {
                    string firmware = ReadFirmware(instance);
                    FanHardwareStatus status = new FanHardwareStatus();
                    status.reachable = true;
                    status.systemProductName = identity.systemProductName;
                    status.baseBoardProduct = identity.baseBoardProduct;
                    status.firmware = firmware;
                    if (!HardwareMatches(identity))
                    {
                        status.writeEnabled = false;
                        status.message = "이 팬 backend는 " + FanHardwareGate.SystemProductName + " / " + FanHardwareGate.BaseBoardProduct +
                            "에서만 활성화됩니다. 감지값: " + (identity.systemProductName ?? "없음") + " / " + (identity.baseBoardProduct ?? "없음");
                        return status;
                    }

                    status.fanMode = ReadByteWith(instance, package, FanModeAddress);
                    status.coolerBoost = (ReadByteWith(instance, package, CoolerBoostAddress) & CoolerBoostMask) != 0;
                    status.cpuTemperature = ReadByteWith(instance, package, CpuTemperatureAddress);
                    status.gpuTemperature = ReadByteWith(instance, package, GpuTemperatureAddress);
                    status.cpuDuty = Math.Min(100, (int)ReadByteWith(instance, package, CpuDutyAddress));
                    status.gpuDuty = Math.Min(100, (int)ReadByteWith(instance, package, GpuDutyAddress));
                    status.cpuRpm = ToRpm(ReadByteWith(instance, package, CpuRpmAddress));
                    status.gpuRpm = ToRpm(ReadByteWith(instance, package, GpuRpmAddress));
                    status.cpuTemperatures = ReadRange(instance, package, 0x69);
                    status.cpuSpeeds = ReadRange(instance, package, 0x72);
                    status.gpuTemperatures = ReadRange(instance, package, 0x81);
                    status.gpuSpeeds = ReadRange(instance, package, 0x8A);
                    status.writeEnabled = HasPlausibleCurve(status);
                    status.message = status.writeEnabled
                        ? "MSI_ACPI WMI, 모델, 보드와 팬 곡선 검증 완료 (읽기/쓰기 가능)."
                        : "팬 모드 또는 곡선 읽기값이 안전 검증을 통과하지 않아 쓰기를 잠갔습니다.";
                    return status;
                });
            }
            catch (Exception exception)
            {
                return new FanHardwareStatus
                {
                    reachable = false,
                    writeEnabled = false,
                    message = "MSI_ACPI WMI를 읽지 못했습니다: " + FriendlyError(exception)
                };
            }
        }

        public FanHardwareStatus ApplyCurve(
            FanPresetDocument configuration,
            int[] cpuTemperatures,
            int[] cpuSpeeds,
            int[] gpuTemperatures,
            int[] gpuSpeeds)
        {
            FanPresetValidator.ValidateCurve(cpuTemperatures, cpuSpeeds, gpuSpeeds);
            lock (_sync)
            {
                FanHardwareStatus before = Query(configuration);
                EnsureWritable(before);
                try
                {
                    WithSession(delegate(ManagementObject instance, ManagementClass package)
                    {
                        SetCoolerBoostWith(instance, package, false);
                        // Release any previous Advanced overlay before replacing the
                        // tables. The GP66 EC can otherwise keep using its cached duty
                        // until the fan-mode byte transitions through Auto.
                        WriteByteWith(instance, package, FanModeAddress, AutoMode);
                        Thread.Sleep(120);
                        WriteRange(instance, package, 0x69, cpuTemperatures);
                        WriteRange(instance, package, 0x72, cpuSpeeds);
                        WriteRange(instance, package, 0x81, gpuTemperatures);
                        WriteRange(instance, package, 0x8A, gpuSpeeds);
                        WriteByteWith(instance, package, FanModeAddress, AdvancedMode);
                        Thread.Sleep(250);
                        return 0;
                    });
                    FanHardwareStatus after = Query(configuration);
                    if (!MatchesCurve(after, cpuTemperatures, cpuSpeeds, gpuTemperatures, gpuSpeeds, AdvancedMode, false))
                    {
                        throw new InvalidOperationException("적용한 팬 곡선의 WMI 읽기 검증이 일치하지 않습니다.");
                    }

                    return after;
                }
                catch (Exception exception)
                {
                    string restoration = TryRestoreAfterFailure(before);
                    throw new InvalidOperationException("팬 곡선 쓰기 중 오류가 발생했습니다. " + restoration, exception);
                }
            }
        }

        public FanHardwareStatus SetAuto(FanPresetDocument configuration)
        {
            lock (_sync)
            {
                FanHardwareStatus before = Query(configuration);
                EnsureWritable(before);
                try
                {
                    WithSession(delegate(ManagementObject instance, ManagementClass package)
                    {
                        SetCoolerBoostWith(instance, package, false);
                        WriteByteWith(instance, package, FanModeAddress, AutoMode);
                        return 0;
                    });
                    FanHardwareStatus after = Query(configuration);
                    if (!MatchesModeAndBoost(after, AutoMode, false))
                    {
                        throw new InvalidOperationException("Auto 모드의 WMI 읽기 검증이 일치하지 않습니다.");
                    }

                    return after;
                }
                catch (Exception exception)
                {
                    string restoration = TryRestoreAfterFailure(before);
                    throw new InvalidOperationException("Auto 모드 설정 실패: " + FriendlyError(exception) + " " + restoration, exception);
                }
            }
        }

        public FanHardwareStatus SetCoolerBoost(FanPresetDocument configuration, bool enabled)
        {
            lock (_sync)
            {
                FanHardwareStatus before = Query(configuration);
                EnsureWritable(before);
                try
                {
                    WithSession(delegate(ManagementObject instance, ManagementClass package)
                    {
                        SetCoolerBoostWith(instance, package, enabled);
                        return 0;
                    });
                    FanHardwareStatus after = Query(configuration);
                    if (after == null || !after.writeEnabled || after.coolerBoost != enabled)
                    {
                        throw new InvalidOperationException("Cooler Boost의 WMI 읽기 검증이 일치하지 않습니다.");
                    }

                    return after;
                }
                catch (Exception exception)
                {
                    string restoration = TryRestoreAfterFailure(before);
                    throw new InvalidOperationException("Cooler Boost 설정 실패: " + FriendlyError(exception) + " " + restoration, exception);
                }
            }
        }

        public FanHardwareStatus Restore(FanPresetDocument configuration, FanBaselineState baseline)
        {
            if (baseline == null || !IsRestorable(baseline))
            {
                throw new InvalidOperationException("저장된 팬 baseline 형식이 안전 검증을 통과하지 않았습니다.");
            }

            lock (_sync)
            {
                FanHardwareStatus current = Query(configuration);
                EnsureWritable(current);
                try
                {
                    WithSession(delegate(ManagementObject instance, ManagementClass package)
                    {
                        WriteRange(instance, package, 0x69, baseline.cpuTemperatures);
                        WriteRange(instance, package, 0x72, baseline.cpuSpeeds);
                        WriteRange(instance, package, 0x81, baseline.gpuTemperatures);
                        WriteRange(instance, package, 0x8A, baseline.gpuSpeeds);
                        WriteByteWith(instance, package, FanModeAddress, (byte)baseline.fanMode);
                        SetCoolerBoostWith(instance, package, baseline.coolerBoost);
                        return 0;
                    });
                    FanHardwareStatus after = Query(configuration);
                    if (!MatchesCurve(
                        after,
                        baseline.cpuTemperatures,
                        baseline.cpuSpeeds,
                        baseline.gpuTemperatures,
                        baseline.gpuSpeeds,
                        baseline.fanMode,
                        baseline.coolerBoost))
                    {
                        throw new InvalidOperationException("복원한 팬 baseline의 WMI 읽기 검증이 일치하지 않습니다.");
                    }

                    return after;
                }
                catch (Exception exception)
                {
                    string restoration = TryRestoreAfterFailure(current);
                    throw new InvalidOperationException("팬 baseline 복원 실패: " + FriendlyError(exception) + " " + restoration, exception);
                }
            }
        }

        private T WithSession<T>(Func<ManagementObject, ManagementClass, T> operation)
        {
            lock (_sync)
            {
                Exception last = null;
                int attempt;
                for (attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureSession();
                        return operation(_instance, _package);
                    }
                    catch (Exception exception)
                    {
                        last = exception;
                        DropSession();
                    }
                }

                throw new InvalidOperationException("MSI_ACPI WMI 세션을 다시 연결하지 못했습니다.", last);
            }
        }

        private void EnsureSession()
        {
            if (_instance != null && _package != null)
            {
                return;
            }

            ManagementObjectSearcher searcher = new ManagementObjectSearcher(WmiNamespace, "SELECT * FROM " + WmiClass);
            foreach (ManagementObject candidate in searcher.Get())
            {
                _instance = candidate;
                break;
            }

            if (_instance == null)
            {
                throw new InvalidOperationException("MSI_ACPI WMI 인스턴스를 찾지 못했습니다. MSI Center/NBFoundation 설치 상태를 확인하세요.");
            }

            _package = new ManagementClass(WmiNamespace, PackageClass, null);
        }

        private void DropSession()
        {
            if (_instance != null)
            {
                _instance.Dispose();
                _instance = null;
            }

            if (_package != null)
            {
                _package.Dispose();
                _package = null;
            }
        }

        private static MsiSystemIdentity ReadSystemIdentity()
        {
            ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                WmiNamespace,
                "SELECT SystemProductName, BaseBoardProduct FROM MS_SystemInformation");
            foreach (ManagementObject item in searcher.Get())
            {
                return new MsiSystemIdentity
                {
                    systemProductName = ToStringValue(item["SystemProductName"]),
                    baseBoardProduct = ToStringValue(item["BaseBoardProduct"])
                };
            }

            throw new InvalidOperationException("MS_SystemInformation WMI 항목을 찾지 못했습니다.");
        }

        private static string ToStringValue(object value)
        {
            return value == null ? String.Empty : Convert.ToString(value).Trim();
        }

        private static string ReadFirmware(ManagementObject instance)
        {
            ManagementBaseObject output = instance.InvokeMethod("Get_EC", null, null);
            if (output == null || output["Data"] == null)
            {
                throw new InvalidOperationException("Get_EC가 펌웨어 데이터를 반환하지 않았습니다.");
            }

            ManagementBaseObject data = (ManagementBaseObject)output["Data"];
            byte[] bytes = (byte[])data["Bytes"];
            if (bytes == null || bytes.Length < 3)
            {
                throw new InvalidOperationException("Get_EC 응답 형식이 올바르지 않습니다.");
            }

            StringBuilder firmware = new StringBuilder();
            int index;
            for (index = 2; index < bytes.Length && bytes[index] != 0; index++)
            {
                if (bytes[index] >= 32 && bytes[index] < 127)
                {
                    firmware.Append((char)bytes[index]);
                }
            }

            return firmware.ToString();
        }

        private static byte ReadByteWith(ManagementObject instance, ManagementClass packageClass, byte address)
        {
            ManagementObject package = packageClass.CreateInstance();
            byte[] bytes = new byte[32];
            bytes[0] = address;
            package["Bytes"] = bytes;
            ManagementBaseObject input = instance.GetMethodParameters("Get_Data");
            input["Data"] = package;
            ManagementBaseObject output = instance.InvokeMethod("Get_Data", input, null);
            if (output == null || output["Data"] == null)
            {
                throw new InvalidOperationException("Get_Data가 값을 반환하지 않았습니다.");
            }

            ManagementBaseObject result = (ManagementBaseObject)output["Data"];
            byte[] returned = (byte[])result["Bytes"];
            if (returned == null || returned.Length < 2)
            {
                throw new InvalidOperationException("Get_Data 응답 형식이 올바르지 않습니다.");
            }

            return returned[1];
        }

        private static void WriteByteWith(ManagementObject instance, ManagementClass packageClass, byte address, byte value)
        {
            ManagementObject package = packageClass.CreateInstance();
            byte[] bytes = new byte[32];
            bytes[0] = address;
            bytes[1] = value;
            package["Bytes"] = bytes;
            ManagementBaseObject input = instance.GetMethodParameters("Set_Data");
            input["Data"] = package;
            instance.InvokeMethod("Set_Data", input, null);
        }

        private static int[] ReadRange(ManagementObject instance, ManagementClass packageClass, byte startAddress)
        {
            int[] values = new int[6];
            int index;
            for (index = 0; index < values.Length; index++)
            {
                values[index] = ReadByteWith(instance, packageClass, (byte)(startAddress + index));
            }

            return values;
        }

        private static void WriteRange(ManagementObject instance, ManagementClass packageClass, byte startAddress, int[] values)
        {
            int index;
            for (index = 0; index < values.Length; index++)
            {
                WriteByteWith(instance, packageClass, (byte)(startAddress + index), (byte)values[index]);
            }
        }

        private static void SetCoolerBoostWith(ManagementObject instance, ManagementClass packageClass, bool enabled)
        {
            byte current = ReadByteWith(instance, packageClass, CoolerBoostAddress);
            byte next = enabled ? (byte)(current | CoolerBoostMask) : (byte)(current & ~CoolerBoostMask);
            WriteByteWith(instance, packageClass, CoolerBoostAddress, next);
        }

        private static bool MatchesModeAndBoost(FanHardwareStatus status, int fanMode, bool coolerBoost)
        {
            return status != null && status.writeEnabled && status.fanMode == fanMode && status.coolerBoost == coolerBoost;
        }

        private static bool MatchesCurve(
            FanHardwareStatus status,
            int[] cpuTemperatures,
            int[] cpuSpeeds,
            int[] gpuTemperatures,
            int[] gpuSpeeds,
            int fanMode,
            bool coolerBoost)
        {
            return MatchesModeAndBoost(status, fanMode, coolerBoost) &&
                SameValues(status.cpuTemperatures, cpuTemperatures) && SameValues(status.cpuSpeeds, cpuSpeeds) &&
                SameValues(status.gpuTemperatures, gpuTemperatures) && SameValues(status.gpuSpeeds, gpuSpeeds);
        }

        private static bool SameValues(int[] left, int[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int index;
            for (index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index]) return false;
            }

            return true;
        }

        private string TryRestoreAfterFailure(FanHardwareStatus backup)
        {
            try
            {
                if (backup == null || !backup.writeEnabled || !HasPlausibleCurve(backup))
                {
                    return "안전한 즉시 복원용 snapshot이 없어 Auto 복원을 시도하지 않았습니다.";
                }

                WithSession(delegate(ManagementObject instance, ManagementClass package)
                {
                    WriteRange(instance, package, 0x69, backup.cpuTemperatures);
                    WriteRange(instance, package, 0x72, backup.cpuSpeeds);
                    WriteRange(instance, package, 0x81, backup.gpuTemperatures);
                    WriteRange(instance, package, 0x8A, backup.gpuSpeeds);
                    WriteByteWith(instance, package, FanModeAddress, (byte)backup.fanMode);
                    SetCoolerBoostWith(instance, package, backup.coolerBoost);
                    return 0;
                });
                return "오류 후 기존 팬 설정 복원을 시도했습니다.";
            }
            catch
            {
                return "오류 후 기존 팬 설정 복원도 실패했습니다. MSI Center에서 Auto를 적용하세요.";
            }
        }

        private static bool HardwareMatches(MsiSystemIdentity identity)
        {
            return identity != null &&
                String.Equals(identity.systemProductName, FanHardwareGate.SystemProductName, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(identity.baseBoardProduct, FanHardwareGate.BaseBoardProduct, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasPlausibleCurve(FanHardwareStatus status)
        {
            return status != null && IsKnownFanMode(status.fanMode) &&
                HasPlausibleTemperatures(status.cpuTemperatures) &&
                HasPlausibleTemperatures(status.gpuTemperatures) &&
                HasPlausibleSpeeds(status.cpuSpeeds) && HasPlausibleSpeeds(status.gpuSpeeds);
        }

        private static bool IsRestorable(FanBaselineState baseline)
        {
            if (baseline.fanMode != AutoMode && baseline.fanMode != 0x1D && baseline.fanMode != AdvancedMode)
            {
                return false;
            }

            FanHardwareStatus status = new FanHardwareStatus
            {
                fanMode = baseline.fanMode,
                coolerBoost = baseline.coolerBoost,
                cpuTemperatures = baseline.cpuTemperatures,
                cpuSpeeds = baseline.cpuSpeeds,
                gpuTemperatures = baseline.gpuTemperatures,
                gpuSpeeds = baseline.gpuSpeeds
            };
            return HasPlausibleCurve(status);
        }

        private static bool IsStrictlyAscending(int[] values)
        {
            if (values == null || values.Length != 6)
            {
                return false;
            }

            int index;
            for (index = 1; index < values.Length; index++)
            {
                if (values[index] <= values[index - 1]) return false;
            }

            return true;
        }

        private static bool HasPlausibleTemperatures(int[] values)
        {
            return values != null && values.Length == 6 && values[0] == 0 && values[5] >= 50 &&
                values.All(delegate(int value) { return value >= 0 && value <= 100; }) && IsStrictlyAscending(values);
        }

        private static bool HasPlausibleSpeeds(int[] values)
        {
            // MSI Center's saved factory/advanced table can contain EC-native values
            // above 100. User presets stay constrained to monotonic 0..100 percent;
            // this readback check only decides whether the current table is safe to
            // snapshot and restore byte-for-byte.
            return values != null && values.Length == 6 &&
                values.All(delegate(int value) { return value >= 0 && value <= 255; });
        }

        private static bool IsKnownFanMode(int value)
        {
            return value == AutoMode || value == 0x1D || value == AdvancedMode;
        }

        private static void EnsureWritable(FanHardwareStatus status)
        {
            if (status == null || !status.writeEnabled)
            {
                throw new InvalidOperationException(status == null ? "팬 backend 상태를 읽지 못했습니다." : status.message);
            }
        }

        private static int ToRpm(int raw)
        {
            if (raw <= 0) return 0;
            int rpm = RpmDivisor / raw;
            return rpm <= MaxPlausibleRpm ? rpm : 0;
        }

        private static string FriendlyError(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }
    }
}
