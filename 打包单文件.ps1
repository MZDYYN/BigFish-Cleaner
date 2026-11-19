# 打包单文件.ps1 —— 把发布目录压成 zip 嵌进启动器，产出一个独立 exe
# 用法：powershell -File 打包单文件.ps1
$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin   = Join-Path $root "bin\Release\net8.0-windows10.0.19041.0\win-x64"
$pub   = Join-Path $bin 'publish'
$stage = Join-Path $root '启动器\stage'
$zip   = Join-Path $root '启动器\payload.zip'
$outDir= Join-Path $root 'dist'
$name  = '大肥鱼C盘清理工具v0.1'

Write-Host '1/5 发布…'
dotnet publish (Join-Path $root 'CleanC.csproj') -c Release -r win-x64 --self-contained false | Out-Null

Write-Host '2/5 准备 payload（含 .xbf）…'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $pub $stage -Recurse -Force
Get-ChildItem $bin -Filter '*.xbf' | ForEach-Object { Copy-Item $_.FullName $stage -Force }
Copy-Item (Join-Path $root 'app.ico') $stage -Force
foreach ($junk in 'rules.json','selection.json') {
    $p = Join-Path $stage $junk
    if (Test-Path $p) { Remove-Item $p -Force }
}
Get-ChildItem $stage -Filter 'clean-log-*' -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host '3/5 压缩…'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host '4/5 编译启动器…'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$exe = Join-Path $outDir ($name + '.exe')
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:$exe `
    /win32manifest:"$(Join-Path $root '启动器\app.manifest')" `
    /win32icon:"$(Join-Path $root 'app.ico')" `
    /resource:"$zip",payload.zip `
    /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    /optimize+ (Join-Path $root '启动器\Launcher.cs')

Write-Host '5/5 清理中间文件…'
Remove-Item $stage -Recurse -Force

Write-Host ("完成：{0}（{1:N1} MB）" -f $exe, ((Get-Item $exe).Length/1MB))
