$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../Assets/Scripts/DeformableImage/WrinkleStrainMath.cs')
function Assert-Close([double]$actual, [double]$expected, [string]$name) {
    if ([Math]::Abs($actual - $expected) -gt 0.00001) { throw "$name failed: $actual != $expected" }
}
function Activation([float]$strain) { [DeformableImage.WrinkleStrainMath]::Activation($strain, 0.008, 0.12) }
Assert-Close ([DeformableImage.WrinkleStrainMath]::SignedStrain(1,1)) 0 'Neutral'
Assert-Close ([DeformableImage.WrinkleStrainMath]::SignedStrain(1,0.9)) -0.1 'Compression sign'
Assert-Close ([DeformableImage.WrinkleStrainMath]::SignedStrain(1,1.1)) 0.1 'Stretch sign'
Assert-Close (Activation 0.1) 0 'Stretch does not generate creases'
Assert-Close (Activation -0.004) 0 'Dead zone'
Assert-Close (Activation -0.12) 1 'Full compression'
Assert-Close (Activation -0.4) 1 'Saturation'
Assert-Close ([DeformableImage.WrinkleStrainMath]::SignedStrain(0,1)) 0 'Degenerate edge'
Assert-Close ([DeformableImage.WrinkleStrainMath]::HorizontalWeight(1,0)) 1 'Horizontal edge'
Assert-Close ([DeformableImage.WrinkleStrainMath]::HorizontalWeight(0,1)) 0 'Vertical edge'
Assert-Close ([DeformableImage.WrinkleStrainMath]::HorizontalWeight(1,1)) 0.5 'Diagonal edge'
# A unit edge translated and rotated remains unit length, so rigid motion cannot activate wrinkles.
$x0=13.0; $y0=-4.0; $x1=13.6; $y1=-3.2
$length=[Math]::Sqrt([Math]::Pow($x1-$x0,2)+[Math]::Pow($y1-$y0,2))
Assert-Close (Activation ([DeformableImage.WrinkleStrainMath]::SignedStrain(1,$length))) 0 'Rigid transform'
$previous=0
foreach($step in 0..40) {
    $value=Activation (-$step/200.0)
    if($value -lt $previous) { throw 'Compression response is not monotonic' }
    $previous=$value
}
Write-Output 'Passed: 12 strain assertions and monotonic compression sweep.'
# Full-face principal strain: horizontal, vertical, mixed-direction, shear and rigid invariance.
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,1,0,1)) 0 'Principal neutral'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,0.81,0,1)) -0.1 'Horizontal compression'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,1,0,0.81)) -0.1 'Vertical compression'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,0.81,0,1.21)) -0.1 'Compression not cancelled by perpendicular stretch'
Assert-Close (Activation ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,1.21,0,1.44))) 0 'Two-axis stretch'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,0.905,0.095,0.905)) -0.1 'Diagonal compression'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(2,0.5,3,2,0.5,3)) 0 'Nonorthogonal triangle neutral'
Assert-Close ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(0,0,1,1,0,1)) 0 'Degenerate reference triangle'
if ((Activation ([DeformableImage.WrinkleStrainMath]::MinimumPrincipalStrain(1,0,1,1,0.3,1.09))) -le 0) { throw 'Shear compression was missed' }
Write-Output 'Passed: 9 principal-strain checks, including all compression directions and shear.'

# Wrinkle activation is stricter than the raw minimum principal strain.
function Wrinkle([float]$aa, [float]$ab, [float]$bb) {
    Activation ([DeformableImage.WrinkleStrainMath]::CompressionOnlyStrain(1,0,1,$aa,$ab,$bb))
}
Assert-Close (Wrinkle 1 0 1) 0 'Wrinkle neutral'
Assert-Close (Wrinkle 1.21 0 1) 0 'Horizontal pulling'
Assert-Close (Wrinkle 1 0 1.44) 0 'Vertical pulling'
Assert-Close (Wrinkle 1.21 0 1.44) 0 'Biaxial pulling'
Assert-Close (Wrinkle 1 0.3 1.09) 0 'Area-preserving shear must not wrinkle'
Assert-Close (Wrinkle 0.81 0 1.44) 0 'Pulling with transverse shortening'
Assert-Close (Wrinkle 0.64 0 1.5625) 0 'Area-preserving mixed stretch'
Assert-Close (Wrinkle 1.21 0.33 1.18) 0 'Pulling combined with shear'
Assert-Close (Wrinkle 0.81 0 1) (Activation -0.1) 'Horizontal squeezing'
Assert-Close (Wrinkle 1 0 0.81) (Activation -0.1) 'Vertical squeezing'
Assert-Close (Wrinkle 0.905 0.095 0.905) (Activation -0.1) 'Diagonal squeezing'
Assert-Close (Wrinkle 0.64 0 1.21) (Activation -0.12) 'Net compression with transverse stretch'
Assert-Close ([DeformableImage.WrinkleStrainMath]::CompressionOnlyStrain(2,0.5,3,2,0.5,3)) 0 'Nonorthogonal rigid metric'
Assert-Close ([DeformableImage.WrinkleStrainMath]::CompressionOnlyStrain(0,0,1,1,0,1)) 0 'Degenerate metric'
Write-Output 'Passed: 14 compression-only regression checks.'
