using JTS.Data;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace JTS_App.Services;

public sealed class AppSettingsService
{
    private const string CredentialTargetPrefix = "JTS.App.Settings.";
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;

    public Task<string?> GetDeepSeekApiKeyAsync() => GetAsync("DeepSeekApiKey", encrypted: true);
    public Task SetDeepSeekApiKeyAsync(string? value) => SetAsync("DeepSeekApiKey", value, encrypted: true);
    public Task<string?> GetDeepSeekModelAsync() => GetAsync("DeepSeek.Model", encrypted: false);
    public Task SetDeepSeekModelAsync(string? value) => SetAsync("DeepSeek.Model", value, encrypted: false);
    public Task<string?> GetDeepSeekThinkingEnabledAsync() => GetAsync("DeepSeek.ThinkingEnabled", encrypted: false);
    public Task SetDeepSeekThinkingEnabledAsync(bool value) => SetAsync("DeepSeek.ThinkingEnabled", value ? "true" : "false", encrypted: false);
    public Task<string?> GetOpenAIApiKeyAsync() => GetAsync("OpenAIApiKey", encrypted: true);
    public Task SetOpenAIApiKeyAsync(string? value) => SetAsync("OpenAIApiKey", value, encrypted: true);
    public Task<string?> GetAppFontFamilyAsync() => GetAsync("AppFontFamily", encrypted: false);
    public Task SetAppFontFamilyAsync(string? value) => SetAsync("AppFontFamily", value, encrypted: false);
    public Task<string?> GetAppFontSizeAsync() => GetAsync("AppFontSize", encrypted: false);
    public Task SetAppFontSizeAsync(double value) =>
        SetAsync("AppFontSize", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);
    public Task<string?> GetD365TenantIdAsync() => GetAsync("D365TenantId", encrypted: false);
    public Task SetD365TenantIdAsync(string? value) => SetAsync("D365TenantId", value, encrypted: false);
    public Task<string?> GetD365ClientIdAsync() => GetAsync("D365ClientId", encrypted: false);
    public Task SetD365ClientIdAsync(string? value) => SetAsync("D365ClientId", value, encrypted: false);
    public Task<string?> GetD365ClientSecretAsync() => GetAsync("D365ClientSecret", encrypted: true);
    public Task SetD365ClientSecretAsync(string? value) => SetAsync("D365ClientSecret", value, encrypted: true);
    public Task<string?> GetD365EnvironmentUrlAsync() => GetAsync("D365EnvironmentUrl", encrypted: false);
    public Task SetD365EnvironmentUrlAsync(string? value) => SetAsync("D365EnvironmentUrl", value, encrypted: false);

    public async Task<string?> GetWhisperModelPathAsync()
    {
        var configured = await GetAsync("WhisperModelPath", encrypted: false);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var turboModel = Path.Combine(AppPaths.ModelsDirectory, "ggml-large-v3-turbo.bin");
        if (File.Exists(turboModel)) return turboModel;

        var baseModel = Path.Combine(AppPaths.ModelsDirectory, "ggml-base.bin");
        return File.Exists(baseModel) ? baseModel : null;
    }
    public Task SetWhisperModelPathAsync(string? value) => SetAsync("WhisperModelPath", value, encrypted: false);
    public Task<string?> GetVoskModelPathAsync() => GetAsync("VoskModelPath", encrypted: false);
    public Task SetVoskModelPathAsync(string? value) => SetAsync("VoskModelPath", value, encrypted: false);
    public Task<string?> GetVoiceSpeakingRateAsync() => GetAsync("Voice.SpeakingRate", encrypted: false);
    public Task SetVoiceSpeakingRateAsync(double value) =>
        SetAsync("Voice.SpeakingRate", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);

    public Task<string?> GetVideoAnalysisModeAsync() => GetAsync("Video.AnalysisMode", encrypted: false);
    public Task SetVideoAnalysisModeAsync(string? value) => SetAsync("Video.AnalysisMode", value, encrypted: false);
    public Task<string?> GetVideoVisualEndpointAsync() => GetAsync("Video.VisualEndpoint", encrypted: false);
    public Task SetVideoVisualEndpointAsync(string? value) => SetAsync("Video.VisualEndpoint", value, encrypted: false);
    public Task<string?> GetVideoVisualModelAsync() => GetAsync("Video.VisualModel", encrypted: false);
    public Task SetVideoVisualModelAsync(string? value) => SetAsync("Video.VisualModel", value, encrypted: false);
    public Task<string?> GetVideoVisualApiKeyAsync() => GetAsync("Video.VisualApiKey", encrypted: true);
    public Task SetVideoVisualApiKeyAsync(string? value) => SetAsync("Video.VisualApiKey", value, encrypted: true);
    public Task<string?> GetVideoVisualMaxFramesAsync() => GetAsync("Video.VisualMaxFrames", encrypted: false);
    public Task SetVideoVisualMaxFramesAsync(int value) =>
        SetAsync("Video.VisualMaxFrames", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);
    public Task<string?> GetVideoVisualFrameIntervalSecondsAsync() => GetAsync("Video.VisualFrameIntervalSeconds", encrypted: false);
    public Task SetVideoVisualFrameIntervalSecondsAsync(int value) =>
        SetAsync("Video.VisualFrameIntervalSeconds", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);
    public Task<string?> GetVideoVisualScanFpsAsync() => GetAsync("Video.VisualScanFps", encrypted: false);
    public Task SetVideoVisualScanFpsAsync(double value) =>
        SetAsync("Video.VisualScanFps", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);

    public Task<string?> GetRemindersEnabledAsync() => GetAsync("Reminders.Enabled", encrypted: false);
    public Task SetRemindersEnabledAsync(bool value) => SetAsync("Reminders.Enabled", value ? "true" : "false", encrypted: false);
    public Task<string?> GetRemindersLeadDaysAsync() => GetAsync("Reminders.LeadDays", encrypted: false);
    public Task SetRemindersLeadDaysAsync(int value) =>
        SetAsync("Reminders.LeadDays", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);
    public Task<string?> GetReminderLastNotifiedAsync() => GetAsync("Reminders.LastNotified", encrypted: false);
    public Task SetReminderLastNotifiedAsync(string? value) => SetAsync("Reminders.LastNotified", value, encrypted: false);

    public Task<string?> GetFocusGoalHoursAsync() => GetAsync("Focus.GoalHours", encrypted: false);
    public Task SetFocusGoalHoursAsync(double value) =>
        SetAsync("Focus.GoalHours", value.ToString(System.Globalization.CultureInfo.InvariantCulture), encrypted: false);
    public Task<string?> GetFocusBarVisibleAsync() => GetAsync("Focus.BarVisible", encrypted: false);
    public Task SetFocusBarVisibleAsync(bool value) => SetAsync("Focus.BarVisible", value ? "true" : "false", encrypted: false);
    public Task<string?> GetFocusBarPositionAsync() => GetAsync("Focus.BarPosition", encrypted: false);
    public Task SetFocusBarPositionAsync(string? value) => SetAsync("Focus.BarPosition", value, encrypted: false);

    public Task<string?> GetAsync(string key, bool encrypted = false)
    {
        return Task.FromResult<string?>(ReadCredential(key));
    }
    public Task SetAsync(string key, string? value, bool encrypted = false)
    {
        WriteCredential(key, value);
        return Task.CompletedTask;
    }

    private static string TargetFor(string key) => CredentialTargetPrefix + key;

    private static string? ReadCredential(string key)
    {
        if (!CredRead(TargetFor(key), CredentialTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null; // ERROR_NOT_FOUND
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    private static void WriteCredential(string key, string? value)
    {
        var target = TargetFor(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!CredDelete(target, CredentialTypeGeneric, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1168) throw new Win32Exception(error);
            }

            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value.Trim());
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = null,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CredentialPersistLocalMachine,
                TargetAlias = null,
                TargetName = target,
                Type = CredentialTypeGeneric,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }
}
