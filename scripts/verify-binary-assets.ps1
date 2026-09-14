param([Parameter(Mandatory = $true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
# Load PE resources as data only. This never executes the service or tray.
if (-not ('EntreeBinaryResources' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class EntreeBinaryResources {
    private delegate bool EnumName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumName callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    public static byte[][] Icons(string path) {
        var module = LoadLibraryEx(path, IntPtr.Zero, 0x22); // AS_DATAFILE | AS_IMAGE_RESOURCE
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var images = new List<byte[]>();
            Exception error = null;
            EnumName callback = delegate(IntPtr loaded, IntPtr type, IntPtr name, IntPtr parameter) {
                try {
                    var resource = FindResource(loaded, name, type);
                    var size = SizeofResource(loaded, resource);
                    var data = LockResource(LoadResource(loaded, resource));
                    if (size == 0 || size > 1024 * 1024 || data == IntPtr.Zero) throw new InvalidOperationException("Invalid icon resource.");
                    var bytes = new byte[(int)size];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    images.Add(bytes);
                    return true;
                } catch (Exception caught) { error = caught; return false; }
            };
            var ok = EnumResourceNames(module, new IntPtr(3), callback, IntPtr.Zero);
            if (error != null) throw error;
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            return images.ToArray();
        } finally { FreeLibrary(module); }
    }
}
'@
}
$iconPath = Join-Path $PSScriptRoot '..\assets\entree-print.ico'
$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $iconPath).Path)
if ([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) { throw 'Invalid source icon.' }
$count = [BitConverter]::ToUInt16($bytes, 4)
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw
$expectedTfm = ([string]$buildProperties.Project.PropertyGroup.TargetFramework).Split('-')[0]
$expectedRuntime = [string]$buildProperties.Project.PropertyGroup.RuntimeFrameworkVersion
$expected = @(for ($i = 0; $i -lt $count; $i++) {
    $entry = 6 + $i * 16
    $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
    $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
    [Convert]::ToBase64String($bytes, $offset, $length)
})
foreach ($relative in @('service\EntreePrintPlugin', 'tray\EntreePrintTray')) {
    $runtime = (Get-Content -LiteralPath (Join-Path $PackageRoot ($relative + '.runtimeconfig.json')) -Raw | ConvertFrom-Json).runtimeOptions
    $frameworkNames = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')
    if ($relative.StartsWith('service\')) { $frameworkNames += 'Microsoft.AspNetCore.App' }
    $frameworks = @($runtime.frameworks)
    if ($runtime.tfm -ne $expectedTfm -or $frameworks.Count -ne $frameworkNames.Count -or
        @(Compare-Object $frameworkNames @($frameworks | ForEach-Object { $_.name })).Count -ne 0 -or
        @($frameworks | Where-Object { $_.version -ne $expectedRuntime }).Count -ne 0) {
        throw "Incorrect runtime requirements: $relative. Expected $expectedTfm / $expectedRuntime."
    }
    $exe = (Resolve-Path -LiteralPath (Join-Path $PackageRoot ($relative + '.exe'))).Path
    foreach ($extension in @('.exe', '.dll')) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PackageRoot ($relative + $extension)))
        if ($info.FileVersion -ne '0.0.1.0' -or -not $info.ProductVersion.StartsWith('0.0.1-beta')) {
            throw "Incorrect binary version: $relative$extension"
        }
    }
    $actual = @([EntreeBinaryResources]::Icons($exe) | ForEach-Object { [Convert]::ToBase64String($_) })
    if ($actual.Count -ne $expected.Count -or @(Compare-Object $expected $actual).Count -ne 0) {
        throw "Published EXE icon differs from the designed printer icon: $relative"
    }
    Write-Host "PASS $relative version 0.0.1-beta; $expectedTfm / $expectedRuntime; $count embedded icon frames match the source asset exactly."
}
