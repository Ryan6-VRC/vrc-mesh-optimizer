using System;
using Unity.Mathematics;

namespace Ryan6Vrc.MeshOptimizer.Core.Gate
{
    /// <summary>
    /// A pinhole camera, for saying how large a distance looks: an offset of so many metres, seen face-on at
    /// <see cref="Distance"/>, spans so many pixels. An offset along the line of sight spans fewer, so the figure is
    /// the most that offset can show.
    /// </summary>
    public struct GateCamera
    {
        public float VerticalFovDegrees;
        /// <summary>Pixel rows across the vertical field of view.</summary>
        public int VerticalPixels;
        /// <summary>Metres from the camera to the surface.</summary>
        public float Distance;

        /// <summary>
        /// 60 degrees over 1080 rows is 18 pixels per degree, the order a current headset resolves at the centre of
        /// view; 1 m is someone standing in front of you.
        /// </summary>
        public static GateCamera Default => new GateCamera { VerticalFovDegrees = 60f, VerticalPixels = 1080, Distance = 1f };

        /// <summary>The height one pixel covers at <see cref="Distance"/>, in metres.</summary>
        public float MetresPerPixel
        {
            get
            {
                if (!(VerticalFovDegrees > 0f && VerticalFovDegrees < 180f) || VerticalPixels < 1 || !(Distance > 0f))
                    throw new InvalidOperationException(
                        $"a camera needs a field of view between 0 and 180 degrees, at least one pixel row and a positive distance; this one has {VerticalFovDegrees} degrees, {VerticalPixels} rows, {Distance} m");
                return 2f * Distance * math.tan(math.radians(VerticalFovDegrees) * 0.5f) / VerticalPixels;
            }
        }

        public float Pixels(float metres) => metres / MetresPerPixel;
    }
}
