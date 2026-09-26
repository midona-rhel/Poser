$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$allowed = @{
    'Poser.Domain' = @()
    'Poser.Documents' = @('Poser.Domain')
    'Poser.Application' = @('Poser.Domain', 'Poser.Documents')
    'Poser.Game' = @('Poser.Domain', 'Poser.Application', 'Poser.Documents')
    'Poser.UI' = @('Poser.Domain', 'Poser.Application', 'Poser.Documents')
    'Poser' = @('Poser.Domain', 'Poser.Documents', 'Poser.Application', 'Poser.Game', 'Poser.UI')
}
foreach ($name in $allowed.Keys) {
    $project = Join-Path $repository "$name/$name.csproj"
    [xml]$definition = Get-Content -LiteralPath $project -Raw
    foreach ($reference in $definition.SelectNodes('//ProjectReference')) {
        $dependency = [IO.Path]::GetFileNameWithoutExtension($reference.Include.Replace('\', '/'))
        if ($dependency -notin $allowed[$name]) {
            throw "$name must not reference $dependency."
        }
    }
    foreach ($reference in $definition.SelectNodes('//Reference | //PackageReference')) {
        $dependency = $reference.Include.Split(',')[0]
        if ($dependency -like 'Poser*' -and $dependency -notin $allowed[$name]) {
            throw "$name bypasses project boundaries through $dependency."
        }
        if ($name -in @('Poser.Domain', 'Poser.Documents', 'Poser.Application') -and
            $dependency -match 'Dalamud|FFXIVClientStructs|ImGui') {
            throw "$name must remain portable, but references $dependency."
        }
    }
    if ($name -in @('Poser.Domain', 'Poser.Documents', 'Poser.Application') -and
        $definition.Project.Sdk -ne 'Microsoft.NET.Sdk') {
        throw "$name must use the portable .NET SDK."
    }
}
if (Test-Path -LiteralPath (Join-Path $repository 'Poser.Core/Poser.Core.csproj')) {
    throw 'The retired Core project must not return.'
}
Write-Output 'Project boundaries passed.'
