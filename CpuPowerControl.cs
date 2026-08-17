using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PowerModeSwitcher
{
    internal enum CpuPowerApplyState
    {
        Verified,
        Unverified,
        Failed
    }

    internal sealed class CpuPowerApplyResult
    {
        public CpuPowerApplyState state { get; set; }
        public string message { get; set; }

        public static CpuPowerApplyResult Verified(string message)
        {
            return new CpuPowerApplyResult { state = CpuPowerApplyState.Verified, message = message };
        }

        public static CpuPowerApplyResult Unverified(string message)
        {
            return new CpuPowerApplyResult { state = CpuPowerApplyState.Unverified, message = message };
        }

        public static CpuPowerApplyResult Failed(string message)
        {
            return new CpuPowerApplyResult { state = CpuPowerApplyState.Failed, message = message };
        }

        public SettingResult ToSettingResult(string setting)
        {
            if (state == CpuPowerApplyState.Verified) return SettingResult.Success(setting, message);
            if (state == CpuPowerApplyState.Unverified) return SettingResult.Unverified(setting, message);
            return SettingResult.Failure(setting, message);
        }
    }

    internal sealed class CpuPowerBackendStatus
    {
        public bool available { get; set; }
        public bool readbackAvailable { get; set; }
        public bool tauSupported { get; set; }
        public bool msrLocked { get; set; }
        public bool mchbarAvailable { get; set; }
        public double? pl1Watts { get; set; }
        public double? pl2Watts { get; set; }
        public double? tauSeconds { get; set; }
        public string powerLimitRaw { get; set; }
        public string powerUnitRaw { get; set; }
        public string cpuFingerprint { get; set; }
        public string message { get; set; }
    }

    internal abstract class CpuPowerBackend
    {
        public abstract CpuPowerBackendStatus Query();
        public abstract CpuPowerApplyResult Apply(int pl1, int pl2, int? tau);
        public abstract CpuPowerApplyResult Restore(string rawPowerLimit, string powerUnitRaw, string cpuFingerprint);
    }

    // Uses the separately installed, signed PawnIO driver and the unmodified official
    // IntelMSR module. No custom driver or MCHBAR write module is used.
    internal sealed class PawnIoIntelPowerBackend : CpuPowerBackend
    {
        private const uint RaplPowerUnitMsr = 0x606;
        private const uint PackagePowerLimitMsr = 0x610;
        private const ulong Pl1PowerMask = 0x7fffUL;
        private const ulong Pl1EnableMask = 1UL << 15;
        private const ulong Pl1TimeMask = 0x7fUL << 17;
        private const ulong LowLockMask = 1UL << 31;
        private const ulong Pl2PowerMask = 0x7fffUL << 32;
        private const ulong Pl2EnableMask = 1UL << 47;
        private const ulong HighLockMask = 1UL << 63;
        private const ulong WritableMask = Pl1PowerMask | Pl1EnableMask | Pl1TimeMask | Pl2PowerMask | Pl2EnableMask;
        private const int MchbarPackageLimitOffset = 0x59a0;
        private const int MaximumPl1Watts = 80;
        private const int MaximumPl2Watts = 100;
        private const int MaximumTauSeconds = 56;

        private readonly object _sync = new object();
        private readonly string _msrModulePath;
        private readonly string _mchbarModulePath;

        public PawnIoIntelPowerBackend(string moduleDirectory)
        {
            _msrModulePath = Path.Combine(moduleDirectory ?? String.Empty, "IntelMSR.bin");
            _mchbarModulePath = Path.Combine(moduleDirectory ?? String.Empty, "IntelMCHBAR.bin");
        }

        public override CpuPowerBackendStatus Query()
        {
            lock (_sync)
            {
                try
                {
                    CpuPowerEligibility eligibility = ReadEligibility();
                    if (!eligibility.supported)
                    {
                        return UnavailableStatus(eligibility.message);
                    }

                    using (new ThreadAffinityScope())
                    {
                        RaplSnapshot snapshot = ReadSnapshot();
                        return ToStatus(snapshot, eligibility);
                    }
                }
                catch (Exception exception)
                {
                    return new CpuPowerBackendStatus
                    {
                        available = false,
                        readbackAvailable = false,
                        tauSupported = false,
                        message = "PawnIO CPU Power backend를 열지 못했습니다. " + FriendlyError(exception) +
                            " 공식 서명 PawnIO 드라이버와 IntelMSR.bin이 필요합니다."
                    };
                }
            }
        }

        public override CpuPowerApplyResult Apply(int pl1, int pl2, int? tau)
        {
            string requestError = ValidateRequest(pl1, pl2, tau);
            if (!String.IsNullOrWhiteSpace(requestError))
            {
                return CpuPowerApplyResult.Failed(requestError);
            }

            RaplSnapshot before = null;
            bool writeAttempted = false;
            lock (_sync)
            {
                try
                {
                    CpuPowerEligibility eligibility = ReadEligibility();
                    if (!eligibility.supported)
                    {
                        return CpuPowerApplyResult.Failed(eligibility.message);
                    }

                    using (new ThreadAffinityScope())
                    {
                        before = ReadSnapshot();
                        if (before.msrLocked)
                        {
                            return CpuPowerApplyResult.Failed(
                                "MSR_PACKAGE_POWER_LIMIT(0x610)이 BIOS/OEM lock 상태입니다. lock bit는 건드리지 않았습니다.");
                        }

                        ulong target = BuildTargetRaw(before, pl1, pl2, tau.Value);
                        WritePackagePowerLimit(target);
                        writeAttempted = true;
                        Thread.Sleep(80);

                        RaplSnapshot after = ReadSnapshot();
                        if ((after.packageLimitRaw & WritableMask) != (target & WritableMask))
                        {
                            return FailAfterWrite(before,
                                "MSR write 후 readback이 일치하지 않습니다. 요청 " + DescribeTarget(pl1, pl2, tau.Value) +
                                " / 결과 " + DescribeSnapshot(after) + ".");
                        }

                        string verified = "MSR 0x610 readback 검증: " + DescribeSnapshot(after) + ".";
                        return CpuPowerApplyResult.Verified(
                            verified + " MCHBAR 값은 상태 표시용으로만 읽었습니다.");
                    }
                }
                catch (Exception exception)
                {
                    string message = "PL1/PL2/Tau 적용 실패: " + FriendlyError(exception);
                    return writeAttempted && before != null
                        ? FailAfterWrite(before, message)
                        : CpuPowerApplyResult.Failed(message);
                }
            }
        }

        public override CpuPowerApplyResult Restore(string rawPowerLimit, string powerUnitRaw, string cpuFingerprint)
        {
            ulong savedRaw;
            if (!TryParseRaw(rawPowerLimit, out savedRaw))
            {
                return CpuPowerApplyResult.Failed("저장된 CPU power MSR snapshot 형식이 올바르지 않습니다.");
            }

            RaplSnapshot before = null;
            bool writeAttempted = false;
            lock (_sync)
            {
                try
                {
                    CpuPowerEligibility eligibility = ReadEligibility();
                    if (!eligibility.supported)
                    {
                        return CpuPowerApplyResult.Failed(eligibility.message);
                    }

                    if (!String.Equals(cpuFingerprint, eligibility.fingerprint, StringComparison.Ordinal))
                    {
                        return CpuPowerApplyResult.Failed("저장된 CPU power snapshot의 CPU/기기 지문이 현재 PC와 달라 복원하지 않았습니다.");
                    }

                    using (new ThreadAffinityScope())
                    {
                        before = ReadSnapshot();
                        if (!String.Equals(powerUnitRaw, FormatRaw(before.powerUnitRaw), StringComparison.OrdinalIgnoreCase))
                        {
                            return CpuPowerApplyResult.Failed("저장된 CPU power snapshot의 RAPL unit이 현재 CPU와 달라 복원하지 않았습니다.");
                        }

                        if (before.msrLocked)
                        {
                            return CpuPowerApplyResult.Failed(
                                "MSR_PACKAGE_POWER_LIMIT(0x610)이 lock 상태여서 저장된 snapshot을 복원하지 않았습니다.");
                        }

                        ulong target = (before.packageLimitRaw & ~WritableMask) | (savedRaw & WritableMask);
                        WritePackagePowerLimit(target);
                        writeAttempted = true;
                        Thread.Sleep(80);
                        RaplSnapshot after = ReadSnapshot();
                        if ((after.packageLimitRaw & WritableMask) != (target & WritableMask))
                        {
                            return FailAfterWrite(before, "CPU power MSR snapshot 복원 후 readback이 일치하지 않습니다.");
                        }

                        return CpuPowerApplyResult.Verified(
                            "저장된 CPU power MSR snapshot의 PL1/PL2/Tau 필드를 복원하고 readback을 확인했습니다: " + DescribeSnapshot(after) + ".");
                    }
                }
                catch (Exception exception)
                {
                    string message = "CPU power MSR snapshot 복원 실패: " + FriendlyError(exception);
                    return writeAttempted && before != null
                        ? FailAfterWrite(before, message)
                        : CpuPowerApplyResult.Failed(message);
                }
            }
        }

        private RaplSnapshot ReadSnapshot()
        {
            using (PawnIoModuleSession msr = PawnIoModuleSession.Open(_msrModulePath))
            {
                RaplSnapshot snapshot = new RaplSnapshot();
                snapshot.powerUnitRaw = msr.ReadMsr(RaplPowerUnitMsr);
                snapshot.packageLimitRaw = msr.ReadMsr(PackagePowerLimitMsr);
                IntelRaplCodec.Decode(snapshot);

                try
                {
                    using (PawnIoModuleSession mchbar = PawnIoModuleSession.Open(_mchbarModulePath))
                    {
                        ulong raw = mchbar.ReadMchbarQword(MchbarPackageLimitOffset);
                        snapshot.mchbarAvailable = true;
                        snapshot.mchbarPl1Raw = (uint)(raw & 0xffffffffUL);
                        snapshot.mchbarPl2Raw = (uint)(raw >> 32);
                        IntelRaplCodec.DecodeMchbar(snapshot);
                    }
                }
                catch
                {
                    // MCHBAR is display-only; MSR write/readback remains usable.
                    snapshot.mchbarAvailable = false;
                }

                return snapshot;
            }
        }

        private void WritePackagePowerLimit(ulong value)
        {
            using (PawnIoModuleSession msr = PawnIoModuleSession.Open(_msrModulePath))
            {
                msr.WriteMsr(PackagePowerLimitMsr, value);
            }
        }

        private static ulong BuildTargetRaw(RaplSnapshot before, int pl1, int pl2, int tau)
        {
            uint pl1Raw = IntelRaplCodec.EncodePower(pl1, before.powerUnitWatts);
            uint pl2Raw = IntelRaplCodec.EncodePower(pl2, before.powerUnitWatts);
            byte tauRaw = IntelRaplCodec.EncodeTimeWindow(tau, before.timeUnitSeconds);

            ulong target = before.packageLimitRaw;
            target = ReplaceField(target, Pl1PowerMask, 0, pl1Raw);
            target |= Pl1EnableMask;
            target = ReplaceField(target, Pl1TimeMask, 17, tauRaw);
            target = ReplaceField(target, Pl2PowerMask, 32, pl2Raw);
            target |= Pl2EnableMask;
            return target;
        }

        private static ulong ReplaceField(ulong value, ulong mask, int shift, ulong replacement)
        {
            return (value & ~mask) | ((replacement << shift) & mask);
        }

        private CpuPowerApplyResult FailAfterWrite(RaplSnapshot before, string reason)
        {
            return CpuPowerApplyResult.Failed(reason + " " + TryRollback(before));
        }

        private string TryRollback(RaplSnapshot before)
        {
            try
            {
                RaplSnapshot current = ReadSnapshot();
                if (current.msrLocked)
                {
                    return "자동 원복 불가: MSR lock 상태입니다. 재부팅 또는 수동 점검이 필요할 수 있습니다.";
                }

                ulong target = (current.packageLimitRaw & ~WritableMask) | (before.packageLimitRaw & WritableMask);
                WritePackagePowerLimit(target);
                Thread.Sleep(80);
                RaplSnapshot after = ReadSnapshot();
                return (after.packageLimitRaw & WritableMask) == (target & WritableMask)
                    ? "이전 PL1/PL2/Tau 값을 자동 원복했습니다."
                    : "자동 원복 readback이 일치하지 않습니다. 재부팅 또는 수동 점검이 필요할 수 있습니다.";
            }
            catch (Exception exception)
            {
                return "자동 원복 실패: " + FriendlyError(exception) + " 재부팅 또는 수동 점검이 필요할 수 있습니다.";
            }
        }

        private static CpuPowerBackendStatus ToStatus(RaplSnapshot snapshot, CpuPowerEligibility eligibility)
        {
            string lockText = snapshot.msrLocked ? " · MSR lock ON" : " · MSR lock OFF";
            string mmioText = snapshot.mchbarAvailable
                ? " · MCHBAR " + DescribeMchbar(snapshot)
                : " · MCHBAR readback unavailable";
            return new CpuPowerBackendStatus
            {
                available = true,
                readbackAvailable = true,
                tauSupported = true,
                msrLocked = snapshot.msrLocked,
                mchbarAvailable = snapshot.mchbarAvailable,
                pl1Watts = snapshot.pl1Watts,
                pl2Watts = snapshot.pl2Watts,
                tauSeconds = snapshot.tauSeconds,
                powerLimitRaw = FormatRaw(snapshot.packageLimitRaw),
                powerUnitRaw = FormatRaw(snapshot.powerUnitRaw),
                cpuFingerprint = eligibility.fingerprint,
                message = "PawnIO RAPL MSR: " + DescribeSnapshot(snapshot) + lockText + mmioText
            };
        }

        private static CpuPowerBackendStatus UnavailableStatus(string message)
        {
            return new CpuPowerBackendStatus
            {
                available = false,
                readbackAvailable = false,
                tauSupported = false,
                message = message
            };
        }

        private static string ValidateRequest(int pl1, int pl2, int? tau)
        {
            if (!tau.HasValue || pl1 <= 0 || pl2 < pl1 || tau.Value <= 0)
            {
                return "PL1·PL2·Tau는 모두 양수여야 하며 PL2는 PL1 이상이어야 합니다.";
            }

            if (pl1 > MaximumPl1Watts || pl2 > MaximumPl2Watts || tau.Value > MaximumTauSeconds)
            {
                return "이 앱의 GP66 안전 범위는 PL1 1~" + MaximumPl1Watts + "W, PL2 1~" +
                    MaximumPl2Watts + "W, Tau 1~" + MaximumTauSeconds + "초입니다.";
            }

            return null;
        }

        private static CpuPowerEligibility ReadEligibility()
        {
            try
            {
                string cpuName = null;
                string cpuManufacturer = null;
                string processorId = null;
                string model = null;
                string board = null;

                using (ManagementObjectSearcher cpuSearch = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT Name, Manufacturer, ProcessorId FROM Win32_Processor"))
                {
                    foreach (ManagementObject cpu in cpuSearch.Get())
                    {
                        cpuName = ReadWmiText(cpu, "Name");
                        cpuManufacturer = ReadWmiText(cpu, "Manufacturer");
                        processorId = ReadWmiText(cpu, "ProcessorId");
                        break;
                    }
                }

                using (ManagementObjectSearcher systemSearch = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject system in systemSearch.Get())
                    {
                        model = ReadWmiText(system, "Model");
                        break;
                    }
                }

                using (ManagementObjectSearcher boardSearch = new ManagementObjectSearcher(
                    "root\\CIMV2", "SELECT Product FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject baseBoard in boardSearch.Get())
                    {
                        board = ReadWmiText(baseBoard, "Product");
                        break;
                    }
                }

                bool cpuMatches = !String.IsNullOrWhiteSpace(cpuName) &&
                    cpuName.IndexOf("i7-11800H", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    String.Equals(cpuManufacturer, "GenuineIntel", StringComparison.OrdinalIgnoreCase);
                bool modelMatches = !String.IsNullOrWhiteSpace(model) &&
                    model.IndexOf("GP66", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    String.Equals(board, "MS-1543", StringComparison.OrdinalIgnoreCase);
                if (!cpuMatches || !modelMatches)
                {
                    return new CpuPowerEligibility
                    {
                        supported = false,
                        message = "CPU Power backend는 GP66 Leopard 11UG / MS-1543 / i7-11800H 전용입니다. 감지값: " +
                            (cpuName ?? "없음") + " / " + (model ?? "없음") + " / " + (board ?? "없음")
                    };
                }

                return new CpuPowerEligibility
                {
                    supported = true,
                    fingerprint = cpuName.Trim() + "|" + (processorId ?? String.Empty).Trim() + "|" +
                        model.Trim() + "|" + board.Trim()
                };
            }
            catch (Exception exception)
            {
                return new CpuPowerEligibility
                {
                    supported = false,
                    message = "CPU/모델 검증에 실패했습니다. " + FriendlyError(exception)
                };
            }
        }

        private static string ReadWmiText(ManagementObject item, string property)
        {
            object value = item == null ? null : item[property];
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
        }

        private static string DescribeTarget(int pl1, int pl2, int tau)
        {
            return "PL1 " + pl1 + "W / PL2 " + pl2 + "W / Tau " + tau + "초";
        }

        private static string DescribeSnapshot(RaplSnapshot snapshot)
        {
            return "PL1 " + IntelRaplCodec.Format(snapshot.pl1Watts) + "W / PL2 " +
                IntelRaplCodec.Format(snapshot.pl2Watts) + "W / Tau " +
                IntelRaplCodec.Format(snapshot.tauSeconds) + "초";
        }

        private static string DescribeMchbar(RaplSnapshot snapshot)
        {
            return "PL1 " + (snapshot.mchbarPl1Enabled ? IntelRaplCodec.Format(snapshot.mchbarPl1Watts) + "W" : "OFF") +
                " / PL2 " + (snapshot.mchbarPl2Enabled ? IntelRaplCodec.Format(snapshot.mchbarPl2Watts) + "W" : "OFF") +
                " / Tau " + IntelRaplCodec.Format(snapshot.mchbarTauSeconds) + "초";
        }

        private static bool TryParseRaw(string value, out ulong raw)
        {
            raw = 0;
            string text = (value ?? String.Empty).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }

            return UInt64.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out raw);
        }

        private static string FormatRaw(ulong value)
        {
            return "0x" + value.ToString("X16", CultureInfo.InvariantCulture);
        }

        private static string FriendlyError(Exception exception)
        {
            Exception current = exception;
            while (current != null && current.InnerException != null &&
                (current is InvalidOperationException || current is Win32Exception))
            {
                current = current.InnerException;
            }

            return current == null || String.IsNullOrWhiteSpace(current.Message)
                ? "알 수 없는 오류"
                : current.Message;
        }
    }

    internal sealed class RaplSnapshot
    {
        public ulong powerUnitRaw;
        public ulong packageLimitRaw;
        public double powerUnitWatts;
        public double timeUnitSeconds;
        public double pl1Watts;
        public double pl2Watts;
        public double tauSeconds;
        public bool msrLocked;
        public bool mchbarAvailable;
        public uint mchbarPl1Raw;
        public uint mchbarPl2Raw;
        public bool mchbarPl1Enabled;
        public bool mchbarPl2Enabled;
        public double mchbarPl1Watts;
        public double mchbarPl2Watts;
        public double mchbarTauSeconds;
    }

    internal sealed class CpuPowerEligibility
    {
        public bool supported;
        public string fingerprint;
        public string message;
    }

    internal static class IntelRaplCodec
    {
        private const ulong PowerMask = 0x7fffUL;
        private const ulong TimeMask = 0x7fUL;

        public static void Decode(RaplSnapshot snapshot)
        {
            int powerExponent = (int)(snapshot.powerUnitRaw & 0x0fUL);
            int timeExponent = (int)((snapshot.powerUnitRaw >> 16) & 0x0fUL);
            if (powerExponent > 15 || timeExponent > 15)
            {
                throw new InvalidOperationException("RAPL unit exponent가 지원 범위를 벗어났습니다.");
            }

            snapshot.powerUnitWatts = 1.0 / Math.Pow(2.0, powerExponent);
            snapshot.timeUnitSeconds = 1.0 / Math.Pow(2.0, timeExponent);
            snapshot.pl1Watts = (snapshot.packageLimitRaw & PowerMask) * snapshot.powerUnitWatts;
            snapshot.pl2Watts = ((snapshot.packageLimitRaw >> 32) & PowerMask) * snapshot.powerUnitWatts;
            snapshot.tauSeconds = DecodeTimeWindow((byte)((snapshot.packageLimitRaw >> 17) & TimeMask), snapshot.timeUnitSeconds);
            snapshot.msrLocked = (snapshot.packageLimitRaw & ((1UL << 31) | (1UL << 63))) != 0;
        }

        public static void DecodeMchbar(RaplSnapshot snapshot)
        {
            snapshot.mchbarPl1Enabled = (snapshot.mchbarPl1Raw & (1U << 15)) != 0;
            snapshot.mchbarPl2Enabled = (snapshot.mchbarPl2Raw & (1U << 15)) != 0;
            snapshot.mchbarPl1Watts = (snapshot.mchbarPl1Raw & 0x7fffU) * snapshot.powerUnitWatts;
            snapshot.mchbarPl2Watts = (snapshot.mchbarPl2Raw & 0x7fffU) * snapshot.powerUnitWatts;
            snapshot.mchbarTauSeconds = DecodeTimeWindow((byte)((snapshot.mchbarPl1Raw >> 17) & 0x7fU), snapshot.timeUnitSeconds);
        }

        public static uint EncodePower(double watts, double powerUnitWatts)
        {
            if (watts <= 0 || powerUnitWatts <= 0)
            {
                throw new InvalidOperationException("PL 전력 단위가 올바르지 않습니다.");
            }

            double encoded = Math.Floor((watts / powerUnitWatts) + 0.0000001);
            if (encoded < 1 || encoded > 0x7fff)
            {
                throw new InvalidOperationException("요청 PL 값이 이 CPU의 RAPL 표현 범위를 벗어났습니다.");
            }

            return (uint)encoded;
        }

        public static byte EncodeTimeWindow(double seconds, double timeUnitSeconds)
        {
            if (seconds <= 0 || timeUnitSeconds <= 0)
            {
                throw new InvalidOperationException("Tau 값 또는 RAPL 시간 단위가 올바르지 않습니다.");
            }

            double closest = -1;
            byte encoded = 0;
            for (int y = 0; y <= 31; y++)
            {
                for (int f = 0; f <= 3; f++)
                {
                    double candidate = Math.Pow(2.0, y) * (1.0 + (f / 4.0)) * timeUnitSeconds;
                    if (candidate <= seconds + 0.0000001 && candidate > closest)
                    {
                        closest = candidate;
                        encoded = (byte)(y | (f << 5));
                    }
                }
            }

            if (closest < 0)
            {
                throw new InvalidOperationException("요청 Tau가 이 CPU의 RAPL 표현 최소값보다 작습니다.");
            }

            return encoded;
        }

        public static double DecodeTimeWindow(byte encoded, double timeUnitSeconds)
        {
            int y = encoded & 0x1f;
            int f = (encoded >> 5) & 0x03;
            return Math.Pow(2.0, y) * (1.0 + (f / 4.0)) * timeUnitSeconds;
        }

        public static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void AssertSelfTest()
        {
            const double powerUnit = 0.125;
            const double timeUnit = 1.0 / 1024.0;
            if (EncodePower(25, powerUnit) != 200)
            {
                throw new InvalidOperationException("RAPL PL 인코딩 self-test에 실패했습니다.");
            }

            foreach (double seconds in new double[] { 4, 8, 16, 28 })
            {
                byte encoded = EncodeTimeWindow(seconds, timeUnit);
                double decoded = DecodeTimeWindow(encoded, timeUnit);
                if (Math.Abs(decoded - seconds) > timeUnit)
                {
                    throw new InvalidOperationException("RAPL Tau 인코딩 self-test에 실패했습니다.");
                }
            }
        }
    }

    internal sealed class PawnIoModuleSession : IDisposable
    {
        private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";
        private const uint GenericReadWrite = 0xc0000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private const uint DeviceType = 41394u << 16;
        private const uint IoctlLoadBinary = DeviceType | (0x821u << 2);
        private const uint IoctlExecute = DeviceType | (0x841u << 2);
        private const int FunctionNameLength = 32;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private IntPtr _handle;

        private PawnIoModuleSession(IntPtr handle)
        {
            _handle = handle;
        }

        public static PawnIoModuleSession Open(string modulePath)
        {
            if (String.IsNullOrWhiteSpace(modulePath) || !File.Exists(modulePath))
            {
                throw new FileNotFoundException("PawnIO module을 찾지 못했습니다.", modulePath);
            }

            IntPtr handle = CreateFile(
                DevicePath,
                GenericReadWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle == InvalidHandleValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO device를 열지 못했습니다.");
            }

            PawnIoModuleSession session = new PawnIoModuleSession(handle);
            try
            {
                session.LoadModule(File.ReadAllBytes(modulePath));
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public ulong ReadMsr(uint msr)
        {
            long[] output = Execute("ioctl_read_msr", new long[] { msr }, 1);
            return unchecked((ulong)output[0]);
        }

        public void WriteMsr(uint msr, ulong value)
        {
            Execute("ioctl_write_msr", new long[] { msr, unchecked((long)value) }, 0);
        }

        public ulong ReadMchbarQword(int offset)
        {
            long[] output = Execute("ioctl_read_qword", new long[] { offset }, 1);
            return unchecked((ulong)output[0]);
        }

        private void LoadModule(byte[] module)
        {
            if (module == null || module.Length == 0)
            {
                throw new InvalidOperationException("PawnIO module 파일이 비어 있습니다.");
            }

            int bytesReturned;
            if (!DeviceIoControl(_handle, IoctlLoadBinary, module, module.Length, null, 0, out bytesReturned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO module을 불러오지 못했습니다.");
            }
        }

        private long[] Execute(string function, long[] values, int outputCount)
        {
            byte[] functionBytes = Encoding.ASCII.GetBytes(function ?? String.Empty);
            if (functionBytes.Length >= FunctionNameLength)
            {
                throw new ArgumentOutOfRangeException("function");
            }

            byte[] input = new byte[FunctionNameLength + ((values == null ? 0 : values.Length) * sizeof(long))];
            Buffer.BlockCopy(functionBytes, 0, input, 0, functionBytes.Length);
            if (values != null)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(values[index]), 0, input,
                        FunctionNameLength + (index * sizeof(long)), sizeof(long));
                }
            }

            byte[] output = outputCount == 0 ? null : new byte[outputCount * sizeof(long)];
            int bytesReturned;
            if (!DeviceIoControl(_handle, IoctlExecute, input, input.Length, output,
                output == null ? 0 : output.Length, out bytesReturned, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO " + function + " 호출에 실패했습니다.");
            }

            if (outputCount == 0)
            {
                return new long[0];
            }

            if (bytesReturned < outputCount * sizeof(long))
            {
                throw new InvalidOperationException("PawnIO " + function + " 결과 길이가 올바르지 않습니다.");
            }

            long[] result = new long[outputCount];
            for (int index = 0; index < outputCount; index++)
            {
                result[index] = BitConverter.ToInt64(output, index * sizeof(long));
            }

            return result;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero && _handle != InvalidHandleValue)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr device,
            uint ioControlCode,
            byte[] inputBuffer,
            int inputBufferSize,
            [Out] byte[] outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class ThreadAffinityScope : IDisposable
    {
        private IntPtr _previousMask;
        private bool _active;

        public ThreadAffinityScope()
        {
            IntPtr currentThread = GetCurrentThread();
            _previousMask = SetThreadAffinityMask(currentThread, new IntPtr(1));
            if (_previousMask == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RAPL MSR 접근용 CPU affinity를 설정하지 못했습니다.");
            }

            _active = true;
        }

        public void Dispose()
        {
            if (_active)
            {
                SetThreadAffinityMask(GetCurrentThread(), _previousMask);
                _active = false;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr SetThreadAffinityMask(IntPtr thread, IntPtr affinityMask);
    }

    internal static class CpuPowerDiagnostic
    {
        public static int Run(string applicationDirectory, string outputPath)
        {
            try
            {
                CpuPowerBackend backend = new PawnIoIntelPowerBackend(Path.Combine(applicationDirectory, "helpers", "PawnIO"));
                CpuPowerBackendStatus status = backend.Query();
                Write(status.message, outputPath);
                return status.available && status.readbackAvailable ? 0 : 1;
            }
            catch (Exception exception)
            {
                Write("FAIL: " + exception.Message, outputPath);
                return 1;
            }
        }

        private static void Write(string message, string outputPath)
        {
            Console.WriteLine(message);
            if (!String.IsNullOrWhiteSpace(outputPath))
            {
                File.WriteAllText(outputPath, message ?? String.Empty, Encoding.UTF8);
            }
        }
    }
}
