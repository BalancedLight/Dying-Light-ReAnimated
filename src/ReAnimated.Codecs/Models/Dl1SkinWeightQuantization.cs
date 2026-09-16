namespace ReAnimated.Codecs.Models;

/// <summary>The source-MSH 15-bit weight encoding shared by export and authoring review.</summary>
public static class Dl1SkinWeightQuantization
{
    public static short[] Encode(ReadOnlySpan<double> weights)
    {
        double total = 0;
        for (int index = 0; index < weights.Length; index++) total += Math.Max(0, weights[index]);
        if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("Skin weights must contain one finite positive value.");
        var result = new short[weights.Length];
        int sum = 0, largest = 0;
        double largestValue = double.NegativeInfinity;
        for (int index = 0; index < weights.Length; index++)
        {
            double normalized = Math.Max(0, weights[index]) / total;
            int value = (int)Math.Floor(normalized * 32767.0);
            result[index] = checked((short)value); sum += value;
            if (normalized > largestValue) { largest = index; largestValue = normalized; }
        }
        result[largest] = checked((short)(result[largest] + (32767 - sum)));
        return result;
    }

    public static double MaximumNormalizedError(ReadOnlySpan<double> weights)
    {
        var encoded = Encode(weights);
        double total = 0, error = 0;
        foreach (double weight in weights) total += Math.Max(0, weight);
        for (int index = 0; index < weights.Length; index++) error = Math.Max(error, Math.Abs(encoded[index] / 32767.0 - Math.Max(0, weights[index]) / total));
        return error;
    }
}
