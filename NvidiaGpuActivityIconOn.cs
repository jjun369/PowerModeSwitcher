using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;

internal static class NvidiaGpuActivityIconOn
{
    private const string RegistryPath = @"Software\NVIDIA Corporation\Global\CoProcManager";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new IntPtr(0xFFFF);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    private static int Main()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("NVIDIA 사용자 설정 키를 열 수 없습니다.");
                }

                key.SetValue("ShowTrayIcon", 1, RegistryValueKind.DWord);
            }

            IntPtr ignored;
            SendMessageTimeout(
                HwndBroadcast,
                WmSettingChange,
                IntPtr.Zero,
                IntPtr.Zero,
                SmtoAbortIfHung,
                1000,
                out ignored);

            MessageBox.Show(
                "NVIDIA GPU Activity 아이콘을 켰습니다.\r\n\r\n트레이의 숨겨진 아이콘에서 확인하세요. 바로 보이지 않으면 로그아웃 또는 재부팅 후 표시됩니다.",
                "NVIDIA GPU Activity",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "설정을 켜지 못했습니다.\r\n\r\n" + exception.Message,
                "NVIDIA GPU Activity",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
