param(
    [Parameter(Mandatory = $true)]
    [string] $VsixPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourcePath = (Resolve-Path -LiteralPath $VsixPath).Path
$outputPath = Join-Path (Split-Path -Parent $sourcePath) 'OwnBridge-x64.vsix'
Copy-Item -LiteralPath $sourcePath -Destination $outputPath -Force

$archive = [System.IO.Compression.ZipFile]::Open($outputPath, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -ieq 'extension.vsixmanifest' } | Select-Object -First 1
    if ($null -eq $entry) {
        throw 'The built VSIX has no extension.vsixmanifest.'
    }

    $entryName = $entry.FullName
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        $manifestText = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $manifest = New-Object System.Xml.XmlDocument
    $manifest.PreserveWhitespace = $true
    $manifest.LoadXml($manifestText)
    $targets = @($manifest.SelectNodes("/*[local-name()='PackageManifest']/*[local-name()='Installation']/*[local-name()='InstallationTarget']"))
    $removed = 0
    $amd64 = 0
    foreach ($target in $targets) {
        $architecture = $target.SelectSingleNode("*[local-name()='ProductArchitecture']")
        if ($null -eq $architecture) { continue }
        if ($architecture.InnerText.Trim() -ieq 'arm64') {
            [void] $target.ParentNode.RemoveChild($target)
            $removed++
        }
        elseif ($architecture.InnerText.Trim() -ieq 'amd64') {
            $amd64++
        }
    }

    if ($removed -lt 1 -or $amd64 -ne 1 -or ($targets.Count - $removed) -ne 1) {
        throw 'Expected exactly one amd64 target and at least one arm64 target. The generated manifest differs from the known Phase 2 package.'
    }

    $entry.Delete()
    $newEntry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.Xml.XmlWriter]::Create($newEntry.Open(), (New-Object System.Xml.XmlWriterSettings))
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created x64-only VSIX: $outputPath"
