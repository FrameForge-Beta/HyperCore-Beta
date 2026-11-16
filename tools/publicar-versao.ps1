# Publica uma versão nova do HyperCore nas Releases do GitHub, para a atualização automática da app a encontrar.
#
# Uso (na pasta do repositório, com o gh autenticado):
#   1. Sobe a versão no HyperCore.csproj (<Version>, <FileVersion>, <AssemblyVersion>), ex.: 0.5.1
#   2. powershell -ExecutionPolicy Bypass -File tools\publicar-versao.ps1 -Notas "O que mudou nesta versão"
#
# O que faz: compila o exe autónomo (não precisa de .NET instalado), calcula o SHA-256, cria a etiqueta vX.Y.Z e a release
# com HyperCore.exe + HyperCore.exe.sha256. As apps já instaladas veem a versão nova na próxima verificação (arranque ou 6 em 6 h).
# A app recusa instalar uma release sem SHA-256 ou cujo exe não seja o HyperCore com a mesma versão.
param(
    [string]$Notas = "",
    [string]$NotasFicheiro = "",
    [string]$Repo = "FrameForge-Beta/FrameForge-Beta",
    [switch]$Rascunho
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

[xml]$proj = Get-Content HyperCore.csproj
$versao = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $versao) { throw "Não encontrei <Version> no HyperCore.csproj" }
$tag = "v$versao"
Write-Host "Versão: $versao ($tag) -> $Repo"

if (gh release view $tag --repo $Repo 2>$null) { throw "A release $tag já existe. Sobe a versão no HyperCore.csproj primeiro." }

$saida = Join-Path $PWD "publicar"
if (Test-Path $saida) { Remove-Item $saida -Recurse -Force }
dotnet publish HyperCore.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $saida -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "A compilação falhou" }

$exe = Join-Path $saida "HyperCore.exe"
$info = (Get-Item $exe).VersionInfo
if ($info.ProductName -ne "HyperCore" -or -not $info.ProductVersion.StartsWith($versao)) {
    throw "O exe não tem o nome/versão esperados ($($info.ProductName) $($info.ProductVersion))"
}
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$sha  HyperCore.exe" | Out-File (Join-Path $saida "HyperCore.exe.sha256") -Encoding ascii -NoNewline
Write-Host "SHA-256: $sha"

$ghArgs = @("release", "create", $tag, $exe, (Join-Path $saida "HyperCore.exe.sha256"), "--repo", $Repo, "--title", "HyperCore $versao")
if ($NotasFicheiro) { $ghArgs += @("--notes-file", $NotasFicheiro) }
elseif ($Notas) { $ghArgs += @("--notes", $Notas) }
else { $ghArgs += @("--generate-notes") }
if ($Rascunho) { $ghArgs += "--draft" }
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create falhou" }
Write-Host "Publicado: https://github.com/$Repo/releases/tag/$tag"
