# 打包.ps1 —— 发布到 dist\ 文件夹版（自动补齐 .xbf）
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$name = '大肥鱼C盘清理工具v0.1'
$bin  = Join-Path $root "bin\Release\net8.0-windows10.0.19041.0\win-x64"
$out  = Join-Path $root "dist\$name"

Write-Host "1/3 编译并发布…"
dotnet publish (Join-Path $root 'CleanC.csproj') -c Release -r win-x64 --self-contained false | Out-Null

Write-Host "2/3 复制到 $out …"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item (Join-Path $bin 'publish\*') $out -Recurse -Force

Write-Host "3/3 补 XAML 编译产物（.xbf）…"
Get-ChildItem $bin -Filter '*.xbf' | ForEach-Object { Copy-Item $_.FullName $out -Force }

Write-Host ("完成：{0}（{1:N1} MB）" -f $out, ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum/1MB))