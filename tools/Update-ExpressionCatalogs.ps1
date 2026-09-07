param(
    [string]$KtisisPath = "$PSScriptRoot/../../Ktisis",
    [string]$Revision = '847f3673'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/../PosingCore/Data/Expressions")
$mapping = @{
    Hyur_Masculine_Midlander = '101_Midlander_Masculine'; Hyur_Feminine_Midlander = '201_Midlander_Feminine'
    Hyur_Masculine_Highlander = '301_Highlander_Masculine'; Hyur_Feminine_Highlander = '401_Highlander_Feminine'
    Elezen_Masculine = '501_Elezen_Masculine'; Elezen_Feminine = '601_Elezen_Feminine'
    Miqote_Masculine = '701_Miqote_Masculine'; Miqote_Feminine = '801_Miqote_Feminine'
    Roegadyn_Masculine_SeaWolf = '901_Roegadyn_Masculine'; Roegadyn_Feminine_SeaWolf = '1001_Roegadyn_Feminine'
    Roegadyn_Masculine_Hellsguard = '901_Roegadyn_Masculine'; Roegadyn_Feminine_Hellsguard = '1001_Roegadyn_Feminine'
    Lalafell_Masculine = '1101_Lalafell_Masculine'; Lalafell_Feminine = '1201_Lalafell_Feminine'
    AuRa_Masculine = '1301_AuRa_Masculine'; AuRa_Feminine = '1401_AuRa_Feminine'
    Hrothgar_Masculine = '1501_Hrothgar_Masculine'; Hrothgar_Feminine = '1601_Hrothgar_Feminine'
    Viera_Masculine = '1701_Viera_Masculine'; Viera_Feminine = '1801_Viera_Feminine'
}
function Vector($text) {
    $numbers = $text.Split(',') | ForEach-Object { [double]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture) }
    $result = [ordered]@{ X = $numbers[0]; Y = $numbers[1]; Z = $numbers[2] }
    if ($numbers.Count -eq 4) { $result.W = $numbers[3] }
    return $result
}
function Bones($source) {
    $result = [ordered]@{}
    if ($null -ne $source) {
        foreach ($bone in $source.PSObject.Properties) {
            $result[$bone.Name] = [ordered]@{
                Position = Vector $bone.Value.Position
                Rotation = Vector $bone.Value.Rotation
                Scale = Vector $bone.Value.Scale
            }
        }
    }
    return $result
}
foreach ($entry in ($mapping.GetEnumerator() | Sort-Object Name)) {
    $path = Join-Path $root "$($entry.Key).json"
    $source = git -C $KtisisPath show "${Revision}:Ktisis/Data/Library/Expressions/$($entry.Value).json"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read $($entry.Value) at $Revision" }
    $data = $source | ConvertFrom-Json
    $units = @($data.Data | Sort-Object Priority | ForEach-Object {
        $label = $_.Id -creplace '([a-z])([A-Z])', '$1 $2'
        if ($_.Pair) { $label = $label -replace ' ([LR])$', ' ($1)' }
        $faces = [ordered]@{}
        if ($_.Skeletons) {
            foreach ($face in $_.Skeletons.PSObject.Properties) { $faces[$face.Name] = Bones $face.Value }
        }
        [ordered]@{ Id = $_.Id; Label = $label; Pair = $_.Pair
            Bones = Bones $_.Transforms; Faces = $faces }
    })
    $output = [ordered]@{ Source = "Ktisis $Revision (GPL-3.0-only)"; Groups = @(@{ Name = 'Face'; Units = $units }) }
    $output | ConvertTo-Json -Depth 15 -Compress | Set-Content -LiteralPath $path -Encoding utf8
}
