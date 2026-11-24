using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;

internal static class MelSpectrogram
{
    public const int SampleRate = 16000;
    public const int SampleCount = 2 * SampleRate;
    public const int Bands = 64;
    public const int Frames = 63;
    private const int FftSize = 2048;
    private const int Hop = 512;
    private static readonly double[] Window = Enumerable.Range(0, FftSize)
        .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize)).ToArray();
    private static readonly float[][] Filters = CreateFilters();

    public static DenseTensor<float> Transform(float[] samples)
    {
        var tensor = new DenseTensor<float>(new[] { 1, Bands, Frames, 1 });
        var spectrum = new Complex[FftSize];
        var power = new float[FftSize / 2 + 1];
        float peak = 1e-10f;
        for (int frame = 0; frame < Frames; frame++)
        {
            int offset = frame * Hop - FftSize / 2;
            for (int i = 0; i < spectrum.Length; i++)
            {
                int index = offset + i;
                spectrum[i] = index >= 0 && index < samples.Length ? samples[index] * Window[i] : 0;
            }
            Fourier.Forward(spectrum, FourierOptions.Matlab);
            for (int bin = 0; bin < power.Length; bin++)
            {
                var value = spectrum[bin];
                power[bin] = (float)(value.Real * value.Real + value.Imaginary * value.Imaginary);
            }

            for (int band = 0; band < Bands; band++)
            {
                float energy = 0;
                for (int bin = 0; bin < power.Length; bin++)
                    energy += Filters[band][bin] * power[bin];
                peak = Math.Max(peak, energy);
                tensor[0, band, frame, 0] = energy;
            }
        }

        double peakDb = 10 * Math.Log10(peak);
        for (int band = 0; band < Bands; band++)
        for (int frame = 0; frame < Frames; frame++)
        {
            double db = 10 * Math.Log10(Math.Max(tensor[0, band, frame, 0], 1e-10f)) - peakDb;
            tensor[0, band, frame, 0] = (float)Math.Clamp((db + 80) / 80, 0, 1);
        }

        return tensor;
    }

    private static float[][] CreateFilters()
    {
        const double logStep = 0.06875177742094912; // ln(6.4) / 27, escala Mel de Slaney.
        double toMaxMel = 15 + Math.Log((SampleRate / 2.0) / 1000) / logStep;
        var edges = new double[Bands + 2];
        for (int index = 0; index < edges.Length; index++)
        {
            double mel = index * toMaxMel / (edges.Length - 1);
            edges[index] = mel < 15 ? mel * 1000 / 15 : 1000 * Math.Exp(logStep * (mel - 15));
        }

        var filters = new float[Bands][];
        for (int band = 0; band < Bands; band++)
        {
            double left = edges[band];
            double center = edges[band + 1];
            double right = edges[band + 2];
            filters[band] = new float[FftSize / 2 + 1];
            for (int bin = 0; bin < filters[band].Length; bin++)
            {
                double frequency = (double)bin * SampleRate / FftSize;
                double triangle = Math.Min((frequency - left) / (center - left), (right - frequency) / (right - center));
                filters[band][bin] = (float)(Math.Max(0, triangle) * 2 / (right - left));
            }
        }
        return filters;
    }
}
