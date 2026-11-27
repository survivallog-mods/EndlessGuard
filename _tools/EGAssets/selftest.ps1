# 磁暴线圈特效离线自测：把 ModShared/TeslaFx*.cs（mod 运行时用的同一份源码）同步到 Assets/Shared，
# 再跑 EGFxSelfTest：几何断言 + URP 下出图（待机×6/充能/放电/冷却）。改特效先过这里，再部署。
# 用法：.\selftest.ps1            （出图在 _candidates\tesla-coil\urp\fx_v2\）
param([string]$Unity = "")

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path (Split-Path $here -Parent) -Parent
$shared = Join-Path $repo "ModShared"
$dst = Join-Path $here "Assets\Shared"

New-Item -ItemType Directory -Force -Path $dst | Out-Null
foreach ($f in @("TeslaFxCoilArcs.cs", "TeslaFxGeo.cs", "TeslaFxRig.cs", "TeslaFxMeshes.cs", "TeslaFxPainter.cs")) {
    Copy-Item (Join-Path $shared $f) (Join-Path $dst $f) -Force
}

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

$log = Join-Path $here "fx-selftest.log"
if (Test-Path $log) { Remove-Item $log -Force }
# Unity.exe 是 GUI 子系统：& 调用不等待，必须 Start-Process 再等它退出（否则日志还没写完就往下走）
$uargs = @("-batchmode", "-quit", "-projectPath", "`"$here`"",
    "-executeMethod", "EGAssets.EGTeslaCoil.FxSelfTest", "-logFile", "`"$log`"")
# 输出重定向到文件：Unity 的子进程（ShaderCompiler 等）会继承管道句柄，不重定向的话外层管道要等它们退出才关
$proc = Start-Process -FilePath $Unity -ArgumentList $uargs -PassThru `
    -RedirectStandardOutput (Join-Path $here "unity-stdout.log") -RedirectStandardError (Join-Path $here "unity-stderr.log")
# 不用 -Wait：它等的是 Unity 及其全部子孙进程，Unity 因编译错误中止时子孙残留，脚本永不返回。
# 退出前先缓存句柄，否则之后读不到 ExitCode
$null = $proc.Handle
while (-not $proc.HasExited) { Start-Sleep -Seconds 2 }
$proc.WaitForExit()
Write-Host "unity exit=$($proc.ExitCode)"
Select-String -Path $log -Pattern "\[FxSelfTest\]|error CS|Exception" | Select-Object -Last 8 | ForEach-Object { $_.Line }
