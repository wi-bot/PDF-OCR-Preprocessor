param(
    [string]$DotNet = 'dotnet',
    [string]$OutputPath = 'C:\EnronAnalysis\Phase0\publish',
    [string[]]$SourceRoots = @('C:\EnronDataset')
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\')
foreach ($protected in @($repository) + $SourceRoots) {
    $protectedPath = [IO.Path]::GetFullPath($protected).TrimEnd('\') + '\'
    if (($output + '\').StartsWith($protectedPath, [StringComparison]::OrdinalIgnoreCase) -or
        $protectedPath.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish output must not overlap source roots or the repository.'
    }
}
for ($ancestor = [IO.DirectoryInfo]::new($output); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-point output paths are not allowed.' }
}
& $DotNet publish (Join-Path $repository 'src/PdfOcrPreprocessor.Desktop') -c Release --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }
$notices = Join-Path $output 'notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$sources = @(
    @{ Name='PdfPig-0.1.16.txt'; Url='https://raw.githubusercontent.com/UglyToad/PdfPig/v0.1.16/LICENSE' },
    @{ Name='PDFtoImage-5.4.0.txt'; Url='https://raw.githubusercontent.com/sungaila/PDFtoImage/v5.4.0/LICENSE' },
    @{ Name='SQLitePCLRaw-2.1.12.txt'; Url='https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/v2.1.12/LICENSE.TXT' },
    @{ Name='Microsoft.Data.Sqlite-10.0.12.txt'; Url='https://raw.githubusercontent.com/dotnet/efcore/v10.0.12/LICENSE.txt' }
)
foreach ($source in $sources) {
    Invoke-WebRequest $source.Url -OutFile (Join-Path $notices $source.Name)
}
foreach ($package in @('skiasharp/4.150.1', 'skiasharp.nativeassets.win32/4.150.1',
    'microsoft.netcore.app.runtime.win-x64/10.0.12', 'microsoft.windowsdesktop.app.runtime.win-x64/10.0.12')) {
    $packagePath = Join-Path $nuget $package
    $packageNotices = Get-ChildItem $packagePath -File | Where-Object { $_.Name -match '(license|third.party|notice)' }
    if (-not $packageNotices) { throw "No notices found for $package" }
    $target = Join-Path $notices ($package.Replace('/', '-'))
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $packageNotices | Copy-Item -Destination $target
}
$archive = Join-Path (Split-Path $output -Parent) 'dependency-notices/pdfium-win-x64-7961.tgz'
New-Item -ItemType Directory -Path (Split-Path $archive -Parent) -Force | Out-Null
if (-not (Test-Path $archive)) {
    Invoke-WebRequest 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F7961/pdfium-win-x64.tgz' -OutFile $archive
}
$pdfium = Join-Path $notices 'PDFium-7961'
New-Item -ItemType Directory -Path $pdfium -Force | Out-Null
& tar -xf $archive -C $pdfium LICENSE licenses VERSION bin/pdfium.dll
if ($LASTEXITCODE -ne 0) { throw 'Could not extract PDFium notices.' }
$packageDll = Join-Path $nuget 'bblanchon.pdfium.win32/152.0.7961/runtimes/win-x64/native/pdfium.dll'
if ((Get-FileHash $packageDll).Hash -ne (Get-FileHash (Join-Path $pdfium 'bin/pdfium.dll')).Hash) {
    throw 'PDFium archive does not match the restored native DLL.'
}
Remove-Item (Join-Path $pdfium 'bin') -Recurse
$sources | ConvertTo-Json | Set-Content (Join-Path $notices 'download-sources.json')
Get-ChildItem $notices -File -Recurse | Get-FileHash -Algorithm SHA256 |
    Select-Object Hash, Path | ConvertTo-Json | Set-Content (Join-Path $output 'notice-hashes.json')
Write-Output "Self-contained proof and notices: $output"