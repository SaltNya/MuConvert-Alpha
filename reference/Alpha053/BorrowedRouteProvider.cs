using System;
using System.Collections.Generic;
using AquaMai.Alpha053.Core;
using AquaMai.Alpha053.FloatMath;
namespace AquaMai.Alpha053.Geometry;
// Equivalent to the supplied JsonDataLoader.TryGetSlideVisualRoute. Only the
// prefab transform positions participate; there are no sensors or judge states.
internal partial class BorrowedRouteProvider
{
    public bool TryGetSlideVisualRoute(string content, out List<Vector3> positions)
    {
        positions = new();
        if (!SlidePathParser.TryParsePath(content, out var path) || path.segments.Count != 1 ||
            !SlideShapeResolver.TryResolve(path.segments[0], out var shape, out _, out _)) return false;
        var segment = path.segments[0];
        var mirrored = shape.StartsWith("-", StringComparison.Ordinal);
        if (mirrored) shape = shape[1..];
        var reverse = shape.StartsWith("r", StringComparison.Ordinal);
        if (reverse) shape = shape[1..];
        if (!Prefabs.TryGetValue(shape, out var prefab)) return false;
        var position = reverse ? segment.endPosition : segment.startPosition;
        var angle = -45f * (mirrored ? position : position - 1) * Mathf.Deg2Rad;
        var cosine = Mathf.Cos(angle); var sine = Mathf.Sin(angle);
        foreach (var bar in prefab)
        {
            var x = mirrored ? -bar.x : bar.x;
            positions.Add(new Vector3(x * cosine - bar.y * sine, x * sine + bar.y * cosine));
        }
        if (reverse) positions.Reverse();
        return positions.Count > 0;
    }
}
internal static class BorrowedSlideDrop
{
    public static Vector3[] BuildAdaptiveTangentCircleRoute(IReadOnlyList<Vector3> source, Vector3 start, Vector3 end, bool centered)
        => BorrowedTangentRoute.Build(source, start, end, centered);
    public static Vector3[] BuildAdaptiveTangentCircleRouteAround(IReadOnlyList<Vector3> source, Vector3 start, Vector3 end, Vector3 center)
        => BorrowedTangentRoute.Build(source, start, end, false, center);
}
