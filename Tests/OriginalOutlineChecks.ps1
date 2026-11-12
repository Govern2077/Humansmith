$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../Assets/Scripts/DeformableImage/OriginalOutlineMath.cs')
function Assert-Equal($actual, $expected, [string]$label) {
    if ($actual -ne $expected) { throw "$label failed: $actual != $expected" }
}
$size = 64
$alpha = New-Object byte[] ($size * $size)
# A source silhouette inset from the image rectangle, with an enclosed hole.
foreach ($y in 8..55) { foreach ($x in 8..55) { $alpha[$y*$size+$x] = 255 } }
foreach ($y in 28..35) { foreach ($x in 28..35) { $alpha[$y*$size+$x] = 0 } }
$integral = [DeformableImage.OriginalOutlineMath]::BuildExteriorIntegral($alpha,$size,$size)
function Touches([float]$x0,[float]$y0,[float]$x1,[float]$y1) {
    [DeformableImage.OriginalOutlineMath]::TouchesExterior($integral,$size,$size,$x0,$y0,$x1,$y1)
}
Assert-Equal (Touches 0.1 0.4 0.2 0.5) $true 'Original alpha outline is anchored'
Assert-Equal (Touches 0.8 0.4 0.9 0.5) $true 'Opposite outline is anchored'
Assert-Equal (Touches 0.4 0.1 0.5 0.2) $true 'Bottom outline is anchored'
Assert-Equal (Touches 0.4 0.8 0.5 0.9) $true 'Top outline is anchored'
Assert-Equal (Touches 0.4 0.4 0.6 0.6) $false 'Internal hole is not an outer anchor'
Assert-Equal (Touches 0.2 0.2 0.3 0.3) $false 'Interior skin remains movable'
Assert-Equal (Touches 0 0 0.03 0.03) $false 'Transparent canvas edge is not the skin edge'
Assert-Equal (Touches 0.12 0.4 0.13 0.5) $true 'Subpixel boundary crossing is covered'
# All support vertices of a crossing triangle must be fixed, including interior vertices.
Assert-Equal (Touches 0.05 0.4 0.3 0.6) $true 'Triangle spanning the silhouette is fixed'
# A separate cut mask must not alter the source-derived anchors.
$cutAlpha = $alpha.Clone()
foreach ($y in 16..25) { $cutAlpha[$y*$size+20] = 0 }
Assert-Equal (Touches 0.28 0.28 0.35 0.35) $false 'New internal incision stays free'
# Fully opaque images still anchor their canvas edge; degenerate sizes stay valid.
$opaque = New-Object byte[] ($size*$size)
for ($i=0; $i -lt $opaque.Length; $i++) { $opaque[$i] = 255 }
$full = [DeformableImage.OriginalOutlineMath]::BuildExteriorIntegral($opaque,$size,$size)
Assert-Equal ([DeformableImage.OriginalOutlineMath]::TouchesExterior($full,$size,$size,0,0.4,0.02,0.6)) $true 'Opaque canvas exterior'
Assert-Equal ([DeformableImage.OriginalOutlineMath]::TouchesExterior($full,$size,$size,0.4,0.4,0.6,0.6)) $false 'Opaque interior'
$single = [DeformableImage.OriginalOutlineMath]::BuildExteriorIntegral([byte[]]@(255),1,1)
Assert-Equal ($single[3]) 1 'Single pixel image'
Write-Output 'Passed: 13 original-outline checks.'
