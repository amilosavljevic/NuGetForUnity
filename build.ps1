param([string]$OutputDirectory = ".\bin")

Import-Module UnitySetup -ErrorAction Stop -MinimumVersion 4.0.97
# Import-Module VSSetup -ErrorAction Stop -MinimumVersion 2.0.1.32208

Write-Host "Build NuGetForUnity " -ForegroundColor Green

# Determine the Unity path for importing project references
$projectVersion = Get-UnityProjectInstance -BasePath ".\Packager" | Select-Object -ExpandProperty Version
$unityPath = Get-UnitySetupInstance | 
    Select-UnitySetupInstance -Version $projectVersion | 
    Select-Object -ExpandProperty Path
if ( !$unityPath -or $unityPath -eq "" ) {
    throw "Could not find Unity editor for $projectVersion"
}

Write-Host "Building package with Unity $projectVersion" -ForegroundColor Green


# Build the NuGetForUnity .dlls
# $vspath = Get-VSSetupInstance | 
#     Select-VSSetupInstance -Require Microsoft.Component.MSBuild -Latest | 
#     Select-Object -ExpandProperty InstallationPath

$msbuild = Get-ChildItem "C:\Program Files\JetBrains\JetBrains Rider*\tools\MSBuild\Current\Bin\" -Filter msbuild.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
if ( !$msbuild -or $msbuild -eq "" ) {
	$msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\" -Filter msbuild.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
	if ( !$msbuild -or $msbuild -eq "" ) {
		throw "Could not find msbuild"
	}
}

$ReferencePath = "$unityPath\Editor\Data\Managed\"
Write-Host "Building CreateDLL MSBuildPath=$msbuild ReferencePath=$ReferencePath" -ForegroundColor Green

& $msbuild ".\CreateDLL\" /nologo /m "/t:restore,rebuild" /p:AppxBundle=Always /p:Platform='Any CPU' /p:Configuration=Release /p:ReferencePath=$ReferencePath | Out-Host
if ( $LASTEXITCODE -ne 0 ) { 
    throw "MSBuild failed with $LASTEXITCODE" 
}


# Copy .dlls from the build into the Packager folder
Copy-Item ".\CreateDLL\bin\Release\NuGetForUnity.dll" ".\Packager\Assets\NuGet\Editor"

# Launch Unity to export the NuGetForUnity package
Start-UnityEditor -Project ".\Packager" -BatchMode -Quit -Wait -ExecuteMethod "NugetForUnity.Export.Execute" -LogFile ".\Packager\NuGetForUnity.unitypackage.log"

# Copy artifacts to output directory
if ( !(Test-Path $OutputDirectory) ) { New-Item -ItemType Directory $OutputDirectory }
Copy-Item ".\CreateDLL\bin\Release\NugetForUnity.*" $OutputDirectory
Copy-Item ".\Packager\NuGetForUnity.unitypackage*" $OutputDirectory
