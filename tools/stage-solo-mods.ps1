[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModsPath)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$modBuild=Join-Path $repo 'src/StardewCompanion.Mod/bin/Release/net6.0'
$fixtureBuild=Join-Path $repo 'tools/Fixture/bin/Release/net6.0'
$protocolBuild=Join-Path $repo 'src/StardewCompanion.Protocol/bin/Release/netstandard2.0/StardewCompanion.Protocol.dll'
$bridge=Join-Path $ModsPath 'StardewCompanion.Bridge'
$fixture=Join-Path $ModsPath 'SoloFiveDayFixture'
$files=@(
    foreach($name in @('StardewCompanion.Mod.dll','StardewCompanion.Fishing.dll','StardewMiningAgent.dll','manifest.json')){
        @{Source=(Join-Path $modBuild $name);Destination=(Join-Path $bridge $name)}
    }
    foreach($name in @('Fixture.dll','manifest.json')){
        @{Source=(Join-Path $fixtureBuild $name);Destination=(Join-Path $fixture $name)}
    }
    @{Source=$protocolBuild;Destination=(Join-Path $bridge 'StardewCompanion.Protocol.dll')}
    @{Source=$protocolBuild;Destination=(Join-Path $fixture 'StardewCompanion.Protocol.dll')}
)
foreach($file in $files){
    if(-not(Test-Path -LiteralPath $file.Source -PathType Leaf)){throw ('Built mod file is missing: '+$file.Source)}
}
New-Item -ItemType Directory -Path $bridge,$fixture -Force|Out-Null
foreach($file in $files){
    Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
    if((Get-FileHash -LiteralPath $file.Source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.Destination -Algorithm SHA256).Hash){
        throw ('Staged mod file differs from the current build: '+$file.Destination)
    }
}
