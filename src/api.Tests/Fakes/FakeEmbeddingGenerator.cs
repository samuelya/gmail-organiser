using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Deterministic unit vectors derived from a SHA-256 of each input; same text, same vector.</summary>
public sealed class FakeEmbeddingGenerator(int dimension = 8) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly List<string> _inputs = [];
    private readonly Lock _gate = new();

    public int Dimension { get; } = dimension > 0 ? dimension : throw new ArgumentOutOfRangeException(nameof(dimension));
    public Exception? Failure { get; set; }
    public bool Disposed { get; private set; }

    public IReadOnlyList<string> Inputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _inputs];
            }
        }
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = values.ToList();
        lock (_gate)
        {
            _inputs.AddRange(list);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(Vector(v)))));
    }

    public float[] Vector(string value)
    {
        var vector = new float[Dimension];
        var block = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        for (var i = 0; i < Dimension; i++)
        {
            if (i > 0 && i % block.Length == 0)
            {
                block = SHA256.HashData(block);
            }

            vector[i] = block[i % block.Length] / 127.5f - 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector : [.. vector.Select(x => x / norm)];
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => Disposed = true;
}
