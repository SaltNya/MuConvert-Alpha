// Reference: MajdataViewAlpha v0.5.3 (GPL-3.0). Parsing/geometry only; no preview judgment runtime.
#nullable disable
#pragma warning disable CS8632
using System;
using System.Collections.Generic;
using AquaMai.Alpha053.Core;
using AquaMai.Alpha053.FloatMath;
namespace AquaMai.Alpha053.Geometry;
internal static class TrajectoryPathGeometry
{
    private const int SamplesPerSegment = 256;

    public static bool TryBuild(
        BorrowedRouteProvider routeSource,
        IReadOnlyList<SlidePathSegmentData> segments,
        List<Vector3> result)
    {
        result.Clear();
        if (segments == null || segments.Count == 0)
            return false;

        foreach (var segment in segments)
        {
            if (segment.shape == "SC")
            {
                var route = new List<Vector3>();
                if (!SlideCodePathGeometry.TryBuild(segment.slideCode, route))
                    return false;
                foreach (var point in route)
                    Add(result, point);
                continue;
            }
            var start = Resolve(segment.start, segment.startPosition, segment.startIsDZone);
            var end = Resolve(segment.end, segment.endPosition, segment.endIsDZone);
            if (segment.shape is "P" or "Q" && segment.hasMiddle)
            {
                var orbit = Resolve(
                    segment.middle,
                    segment.middlePosition,
                    segment.middleIsDZone);
                SelectableOrbitPathGeometry.Append(
                    routeSource,
                    result,
                    start.Area,
                    start.Position,
                    start.IsDZone,
                    orbit.Area,
                    orbit.Position,
                    orbit.IsDZone,
                    segment.middle.source?.Length == 1 &&
                    char.IsDigit(segment.middle.source[0]),
                    end.Area,
                    end.Position,
                    end.IsDZone,
                    segment.shape);
                continue;
            }
            if (start.Area == 'K' && end.Area == 'K' &&
                routeSource != null &&
                routeSource.TryGetSlideVisualRoute(
                    segment.ToExpression(includeDZone: true), out var exact) &&
                exact.Count > 0)
            {
                Add(result, Position(start.Area, start.Position, start.IsDZone));
                foreach (var point in exact)
                    Add(result, point);
                Add(result, Position(end.Area, end.Position, end.IsDZone));
                continue;
            }

            AppendFallback(routeSource, segment, start, end, result);
        }
        return result.Count >= 2;
    }

    private static void AppendFallback(
        BorrowedRouteProvider routeSource,
        SlidePathSegmentData segment,
        (char Area, int Position, bool IsDZone) start,
        (char Area, int Position, bool IsDZone) end,
        List<Vector3> result)
    {
        var startPoint = Position(start.Area, start.Position, start.IsDZone);
        var endPoint = Position(end.Area, end.Position, end.IsDZone);
        var first = result.Count == 0 ? 0 : 1;
        if (segment.shape == "V" && segment.hasMiddle)
        {
            var middle = Resolve(
                segment.middle, segment.middlePosition, segment.middleIsDZone);
            AppendLine(result, startPoint,
                Position(middle.Area, middle.Position, middle.IsDZone), first,
                SamplesPerSegment / 2);
            AppendLine(result,
                Position(middle.Area, middle.Position, middle.IsDZone), endPoint,
                1, SamplesPerSegment / 2);
            return;
        }
        if (segment.shape == "-" || segment.shape == "v")
        {
            if (segment.shape == "v")
            {
                AppendLine(result, startPoint, Vector3.zero, first,
                    SamplesPerSegment / 2);
                AppendLine(result, Vector3.zero, endPoint, 1,
                    SamplesPerSegment / 2);
            }
            else
            {
                AppendLine(result, startPoint, endPoint, first,
                    SamplesPerSegment);
            }
            return;
        }

        if (routeSource != null &&
            routeSource.TryGetSlideVisualRoute(
                $"{start.Position}{segment.shape}{end.Position}[4:1]",
                out var sourcePath) && sourcePath.Count > 0)
        {
            var originalStart = Position('K', start.Position, start.IsDZone);
            var originalEnd = Position('K', end.Position, end.IsDZone);
            for (var i = first; i <= sourcePath.Count + 1; i++)
            {
                var source = i == 0 ? originalStart :
                    i == sourcePath.Count + 1 ? originalEnd : sourcePath[i - 1];
                var t = i / (float)(sourcePath.Count + 1);
                var offset = Vector3.Lerp(
                    startPoint - originalStart,
                    endPoint - originalEnd,
                    t);
                Add(result, source + offset);
            }
            return;
        }

        AppendArc(result, startPoint, endPoint, segment.shape, start.Position, first);
    }

    private static void AppendArc(
        List<Vector3> result,
        Vector3 start,
        Vector3 end,
        string shape,
        int startPosition,
        int first)
    {
        var direction = shape.StartsWith("<", StringComparison.Ordinal) ? '<' : '>';
        var loops = Math.Max(1, shape.Length);
        var startAngle = Mathf.Atan2(start.y, start.x);
        var endAngle = Mathf.Atan2(end.y, end.x);
        var delta = (float)TouchSlideDirection.Sweep(startAngle, endAngle, startPosition, direction);
        var sign = Mathf.Sign(delta);
        delta += sign * Mathf.PI * 2f * (loops - 1);
        var samples = SamplesPerSegment * loops;
        for (var i = first; i <= samples; i++)
        {
            var t = i / (float)samples;
            var angle = startAngle + delta * t;
            var radius = Mathf.Lerp(start.magnitude, end.magnitude, t);
            Add(result, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
    }

    private static void AppendLine(
        List<Vector3> result,
        Vector3 start,
        Vector3 end,
        int first,
        int samples)
    {
        for (var i = first; i <= samples; i++)
            Add(result, Vector3.Lerp(start, end, i / (float)samples));
    }

    private static void Add(List<Vector3> result, Vector3 point)
    {
        if (result.Count == 0 ||
            (result[^1] - point).sqrMagnitude > 0.000001f)
            result.Add(point);
    }

    private static (char Area, int Position, bool IsDZone) Resolve(
        SlidePositionData parsed,
        int legacyPosition,
        bool legacyDZone)
    {
        return parsed != null && parsed.position != 0
            ? (parsed.area, parsed.position, parsed.isDZone)
            : ('K', legacyPosition, legacyDZone);
    }

    private static Vector3 Position(char area, int index, bool dZone)
    {
        if (area == 'C')
            return Vector3.zero;
        var angleOffset = area is 'A' or 'B' or 'K'
            ? Mathf.PI * 5f / 8f
            : Mathf.PI * 6f / 8f;
        if (area == 'K' && dZone)
            angleOffset += Mathf.PI / 8f;
        var radius = area switch
        {
            'K' => 4.8f,
            'A' or 'D' => 4.1f,
            'B' => 2.3f,
            'E' => 3f,
            _ => 0f
        };
        var angle = -index * Mathf.PI / 4f + angleOffset;
        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }
}