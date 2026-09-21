using System.Numerics;
using FreiAtlas.Core.Atlas;

namespace FreiAtlas.Atlas.Graph;

internal static class AtlasGridPositionNormalizer
{
    private const float MinimumEnvelopeRadius = 5_000f;
    private const float EnvelopeMadMultiplier = 12f;

    public static IReadOnlyList<AtlasNodeSnapshot> Normalize(
        IReadOnlyList<AtlasNodeSnapshot> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (nodes.Count < 4
            || !TryBuildEnvelope(nodes, out var envelope)
            || !TryFitAffine(nodes, envelope, out var transform))
        {
            return nodes;
        }

        AtlasNodeSnapshot[]? normalized = null;
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (!IsFogged(node) || envelope.Contains(PositionOf(node)))
            {
                continue;
            }

            var projected = transform.Project(node.Grid);
            if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y))
            {
                continue;
            }

            normalized ??= nodes.ToArray();
            normalized[index] = node with
            {
                RelativeX = projected.X,
                RelativeY = projected.Y
            };
        }

        return normalized ?? nodes;
    }

    private static bool TryBuildEnvelope(
        IEnumerable<AtlasNodeSnapshot> nodes,
        out PositionEnvelope envelope)
    {
        var positions = nodes
            .Select(PositionOf)
            .Where(IsFinite)
            .ToArray();
        if (positions.Length < 3)
        {
            envelope = default;
            return false;
        }

        var medianX = Median(positions.Select(position => position.X));
        var medianY = Median(positions.Select(position => position.Y));
        var radiusX = MathF.Max(
            MinimumEnvelopeRadius,
            Median(positions.Select(position => MathF.Abs(position.X - medianX)))
            * EnvelopeMadMultiplier);
        var radiusY = MathF.Max(
            MinimumEnvelopeRadius,
            Median(positions.Select(position => MathF.Abs(position.Y - medianY)))
            * EnvelopeMadMultiplier);

        envelope = new PositionEnvelope(
            medianX - radiusX,
            medianX + radiusX,
            medianY - radiusY,
            medianY + radiusY);
        return true;
    }

    private static bool TryFitAffine(
        IEnumerable<AtlasNodeSnapshot> nodes,
        PositionEnvelope envelope,
        out AffineGridTransform transform)
    {
        var anchors = nodes
            .Where(node => envelope.Contains(PositionOf(node)))
            .ToArray();
        if (anchors.Length < 3)
        {
            transform = default;
            return false;
        }

        var matrix = new double[3, 3];
        var vectorX = new double[3];
        var vectorY = new double[3];
        foreach (var node in anchors)
        {
            var gx = (double)node.Grid.X;
            var gy = (double)node.Grid.Y;
            var basis = new[] { gx, gy, 1d };
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    matrix[row, column] += basis[row] * basis[column];
                }

                vectorX[row] += basis[row] * node.RelativeX;
                vectorY[row] += basis[row] * node.RelativeY;
            }
        }

        if (!TrySolve3x3(matrix, vectorX, out var coeffX)
            || !TrySolve3x3(matrix, vectorY, out var coeffY))
        {
            transform = default;
            return false;
        }

        transform = new AffineGridTransform(
            (float)coeffX[0],
            (float)coeffX[1],
            (float)coeffX[2],
            (float)coeffY[0],
            (float)coeffY[1],
            (float)coeffY[2]);
        return true;
    }

    private static bool TrySolve3x3(
        double[,] sourceMatrix,
        double[] sourceVector,
        out double[] result)
    {
        var matrix = (double[,])sourceMatrix.Clone();
        var vector = (double[])sourceVector.Clone();
        result = new double[3];

        for (var pivot = 0; pivot < 3; pivot++)
        {
            var bestRow = pivot;
            var bestValue = Math.Abs(matrix[pivot, pivot]);
            for (var row = pivot + 1; row < 3; row++)
            {
                var value = Math.Abs(matrix[row, pivot]);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestRow = row;
                }
            }

            if (bestValue < 1e-9)
            {
                return false;
            }

            if (bestRow != pivot)
            {
                for (var column = pivot; column < 3; column++)
                {
                    (matrix[pivot, column], matrix[bestRow, column]) =
                        (matrix[bestRow, column], matrix[pivot, column]);
                }

                (vector[pivot], vector[bestRow]) =
                    (vector[bestRow], vector[pivot]);
            }

            var pivotValue = matrix[pivot, pivot];
            for (var column = pivot; column < 3; column++)
            {
                matrix[pivot, column] /= pivotValue;
            }

            vector[pivot] /= pivotValue;

            for (var row = 0; row < 3; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = matrix[row, pivot];
                for (var column = pivot; column < 3; column++)
                {
                    matrix[row, column] -= factor * matrix[pivot, column];
                }

                vector[row] -= factor * vector[pivot];
            }
        }

        result[0] = vector[0];
        result[1] = vector[1];
        result[2] = vector[2];
        return result.All(value => double.IsFinite(value));
    }

    private static Vector2 PositionOf(AtlasNodeSnapshot node)
        => new(node.RelativeX, node.RelativeY);

    private static bool IsFogged(AtlasNodeSnapshot node)
        => !node.IsVisible
           || node.IsDiscovered is false;

    private static bool IsFinite(Vector2 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y);

    private static float Median(IEnumerable<float> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private readonly record struct PositionEnvelope(
        float MinX,
        float MaxX,
        float MinY,
        float MaxY)
    {
        public bool Contains(Vector2 position)
            => float.IsFinite(position.X)
               && float.IsFinite(position.Y)
               && position.X >= MinX
               && position.X <= MaxX
               && position.Y >= MinY
               && position.Y <= MaxY;
    }

    private readonly record struct AffineGridTransform(
        float Xx,
        float Xy,
        float X0,
        float Yx,
        float Yy,
        float Y0)
    {
        public Vector2 Project(AtlasGridPos grid)
            => new(
                Xx * grid.X + Xy * grid.Y + X0,
                Yx * grid.X + Yy * grid.Y + Y0);
    }
}
