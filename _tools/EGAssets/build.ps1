# 磁暴线圈 AssetBundle 构建：外部模型 → prefab + 材质 → BundleOutput/endlessguard_tesla
# 用法：.\build.ps1                      （默认 BuildBundle；外部模型或材质、shader 改动后必须重打，游戏里跑的是这个包）
#       .\build.ps1 -Method EGAssets.EGTeslaCoil.DumpParts   （导出部件包围盒，取特效锚点数值）
param([string]$Unity = "", [string]$Method = "EGAssets.EGTeslaCoil.BuildBundle")

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

function Find-Unity {
    $candidates = @(
        "D:\Unity\6000.2.7f2\Editor\Unity.exe",
        "C:\Program Files\Unity\Hub\Editor\6000.2.7f2\Editor\Unity.exe",
        "D:\Program Files\Unity\Hub\Editor\6000.2.7f2\Editor\Unity.exe",
        "E:\Program Files\Unity\Hub\Editor\6000.2.7f2\Editor\Unity.exe"
    )
    foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
    return $null
}

if (-not $Unity) { $Unity = Find-Unity }
if (-not $Unity -or -not (Test-Path $Unity)) { Write-Host "找不到 Unity 6000.2.7f2"; exit 2 }

$log = Join-Path $here "build.log"
if (Test-Path $log) { Remove-Item $log -Force }
# Unity.exe 是 GUI 子系统：& 调用不等待，必须 Start-Process 再等它退出（否则日志还没写完就往下走）
$uargs = @("-batchmode", "-quit", "-projectPath", "`"$here`"",
    "-executeMethod", $Method, "-logFile", "`"$log`"")
# 输出重定向到文件：Unity 的子进程（ShaderCompiler 等）会继承管道句柄，不重定向的话外层管道要等它们退出才关
$proc = Start-Process -FilePath $Unity -ArgumentList $uargs -PassThru `
    -RedirectStandardOutput (Join-Path $here "unity-stdout.log") -RedirectStandardError (Join-Path $here "unity-stderr.log")
# 不用 -Wait：它等的是 Unity 及其全部子孙进程，Unity 因编译错误中止时子孙残留，脚本永不返回。
# 退出前先缓存句柄，否则之后读不到 ExitCode
$null = $proc.Handle
while (-not $proc.HasExited) { Start-Sleep -Seconds 2 }
$proc.WaitForExit()
Write-Host "unity exit=$($proc.ExitCode)"
Select-String -Path $log -Pattern "\[EGAssets\]|\[Parts\]|error CS|FAILED" | Select-Object -Last 12 | ForEach-Object { $_.Line }
