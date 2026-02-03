$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$CrateDir = Join-Path $Root 'native/lean-crypto-ffi'

cargo build --release --manifest-path (Join-Path $CrateDir 'Cargo.toml')

$Rid = 'win-x64'
$OutDir = Join-Path $Root "src/Lean.Client/runtimes/$Rid/native"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$LibPath = Join-Path $CrateDir 'target/release/lean_crypto_ffi.dll'
Copy-Item -Force $LibPath (Join-Path $OutDir 'lean_crypto_ffi.dll')
Write-Host "Copied lean_crypto_ffi.dll to $OutDir"
