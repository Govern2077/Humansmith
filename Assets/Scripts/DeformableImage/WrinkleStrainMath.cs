using System;

namespace DeformableImage
{
    public static class WrinkleStrainMath
    {
        // Artistic compression-only criterion: directional shortening must also
        // reduce surface area. Pure shear and tension-dominated mouth motion can
        // have a negative minor principal strain without net skin compression.
        public static float CompressionOnlyStrain(float restAA, float restAB, float restBB,
            float currentAA, float currentAB, float currentBB)
        {
            double restDeterminant = (double)restAA * restBB - (double)restAB * restAB;
            if (restDeterminant <= 1e-20) return 0f;
            double currentDeterminant = Math.Max(0.0,
                (double)currentAA * currentBB - (double)currentAB * currentAB);
            float areaStrain = (float)(Math.Sqrt(currentDeterminant / restDeterminant) - 1.0);
            return Math.Max(areaStrain, MinimumPrincipalStrain(restAA, restAB, restBB,
                currentAA, currentAB, currentBB));
        }

        // Smallest in-surface principal stretch from current/rest triangle Gram matrices.
        // Uses intrinsic 3D lengths and dot products, so rigid rotation and translation cancel.
        public static float MinimumPrincipalStrain(float restAA, float restAB, float restBB,
            float currentAA, float currentAB, float currentBB)
        {
            double determinant = (double)restAA * restBB - (double)restAB * restAB;
            if (determinant <= 1e-20) return 0f;
            double trace = ((double)currentAA * restBB + (double)currentBB * restAA
                - 2.0 * currentAB * restAB) / determinant;
            double product = Math.Max(0.0, ((double)currentAA * currentBB - (double)currentAB * currentAB) / determinant);
            double discriminant = Math.Max(0.0, trace * trace - 4.0 * product);
            double maximum = Math.Max(0.0, (trace + Math.Sqrt(discriminant)) * 0.5);
            double minimum = maximum > 1e-20 ? product / maximum : 0.0;
            return (float)(Math.Sqrt(Math.Max(0.0, minimum)) - 1.0);
        }

        // Dimensionless signed strain: negative compression, positive stretch.
        public static float SignedStrain(float restLength, float currentLength)
        {
            return restLength <= 0.000001f ? 0f : currentLength / restLength - 1f;
        }

        public static float HorizontalWeight(float du, float dv)
        {
            float total = du * du + dv * dv;
            return total <= 0.0000000001f ? 0f : du * du / total;
        }

        public static float Activation(float signedStrain, float threshold, float fullCompression)
        {
            float t = Math.Max(0f, Math.Min(1f, (-signedStrain - threshold) / Math.Max(0.0001f, fullCompression - threshold)));
            return t * t * (3f - 2f * t);
        }
    }
}
