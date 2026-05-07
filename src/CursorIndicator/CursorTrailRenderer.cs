using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace CursorTrailIndicators;

internal sealed class CursorTrailRenderer
{
    private readonly Configuration configuration;
    private readonly List<Particle> particles = [];
    private readonly Random random = new();
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private Vector2? lastTrailMousePosition;
    private Vector2? lastShakeMousePosition;
    private Vector2? shakeOriginPosition;
    private Vector2? lastShakeDirection;
    private Vector2 revealRingPosition;
    private float ringRevealRemainingSeconds;
    private float shakeWindowSeconds;
    private float shakePathLength;
    private int shakeDirectionChanges;
    private int shakeMovementSamples;
    private bool hasRevealRingPosition;
    private bool currentFrameInCombat;
    private double lastFrameSeconds;

    public CursorTrailRenderer(Configuration configuration)
    {
        this.configuration = configuration;
        lastFrameSeconds = stopwatch.Elapsed.TotalSeconds;
    }

    public void Draw(bool inCombat)
    {
        currentFrameInCombat = inCombat;

        var now = stopwatch.Elapsed.TotalSeconds;
        var deltaSeconds = Math.Min((float)(now - lastFrameSeconds), 0.05f);
        lastFrameSeconds = now;

        var io = ImGui.GetIO();
        var mousePosition = io.MousePos;
        if (configuration.HideWhenMouseOutsideViewport && !IsInsideViewport(mousePosition, io.DisplaySize))
        {
            lastTrailMousePosition = null;
            lastShakeMousePosition = null;
            shakeOriginPosition = null;
            lastShakeDirection = null;
            ResetShakeMetrics();
            hasRevealRingPosition = false;
            ringRevealRemainingSeconds = 0f;
            particles.Clear();
            return;
        }

        var showTrail = configuration.ShowTrail
            && (!configuration.OnlyShowTrailDuringCombat || inCombat);

        if (showTrail)
        {
            SpawnTrail(mousePosition);
        }
        else
        {
            lastTrailMousePosition = null;
            RemoveTrailParticles();
        }

        UpdateRingReveal(mousePosition, deltaSeconds, IsAnyMouseButtonDown(io));

        UpdateParticles(deltaSeconds);
        DrawParticles();

        if (ShouldDrawRing)
            DrawCursorRing(mousePosition, (float)now, deltaSeconds);
    }

    private void SpawnTrail(Vector2 mousePosition)
    {
        if (lastTrailMousePosition is not { } previous)
        {
            lastTrailMousePosition = mousePosition;
            AddParticle(mousePosition, Vector2.Zero, 1f);
            return;
        }

        var delta = mousePosition - previous;
        var distance = delta.Length();
        if (distance < configuration.TrailSpacing)
            return;

        var direction = delta / distance;
        var count = Math.Min((int)(distance / configuration.TrailSpacing), 8);
        for (var i = 1; i <= count; i++)
        {
            var position = previous + direction * (configuration.TrailSpacing * i);
            var jitter = new Vector2(RandomSigned(2.2f), RandomSigned(2.2f));
            AddParticle(position + jitter, -direction * RandomRange(8f, 24f), RandomRange(0.65f, 1f));
        }

        lastTrailMousePosition = mousePosition;
    }

    private void AddParticle(Vector2 position, Vector2 velocity, float sizeScale)
    {
        particles.Add(new Particle(
            position,
            velocity,
            configuration.ParticleLifetimeSeconds,
            configuration.ParticleLifetimeSeconds,
            configuration.ParticleSize * sizeScale));

        var overflow = particles.Count - configuration.MaxParticles;
        if (overflow > 0)
            particles.RemoveRange(0, overflow);
    }

    private void RemoveTrailParticles()
    {
        particles.Clear();
    }

    private void UpdateRingReveal(Vector2 mousePosition, float deltaSeconds, bool mouseButtonDown)
    {
        if (ringRevealRemainingSeconds > 0f)
        {
            ringRevealRemainingSeconds = Math.Max(0f, ringRevealRemainingSeconds - deltaSeconds);
        }

        if (!CanRevealRing)
        {
            ResetShakeAttempt(mousePosition);
            ringRevealRemainingSeconds = 0f;
            return;
        }

        if (ringRevealRemainingSeconds > 0f)
        {
            ResetShakeAttempt(mousePosition);
            return;
        }

        if (mouseButtonDown)
        {
            ResetShakeAttempt(mousePosition);
            return;
        }

        if (lastShakeMousePosition is not { } previous || shakeOriginPosition is not { } origin)
        {
            ResetShakeAttempt(mousePosition);
            return;
        }

        if (deltaSeconds <= 0f)
        {
            return;
        }

        var delta = mousePosition - previous;
        var distance = delta.Length();
        if (distance < MinShakeStepDistance)
        {
            shakeWindowSeconds += deltaSeconds;
            if (shakeWindowSeconds > ShakeWindowSeconds)
                ResetShakeAttempt(mousePosition);

            return;
        }

        if (distance > MaxShakeStepDistance || Vector2.Distance(mousePosition, origin) > ShakeRadius)
        {
            ResetShakeAttempt(mousePosition);
            return;
        }

        shakeWindowSeconds += deltaSeconds;
        if (shakeWindowSeconds > ShakeWindowSeconds)
        {
            ResetShakeAttempt(mousePosition);
            return;
        }

        var direction = delta / distance;
        if (lastShakeDirection is { } previousDirection
            && Vector2.Dot(direction, previousDirection) < ShakeDirectionChangeDot)
        {
            shakeDirectionChanges++;
        }

        lastShakeDirection = direction;
        lastShakeMousePosition = mousePosition;
        shakePathLength += distance;
        shakeMovementSamples++;

        if (!IsShakeTriggered(mousePosition, origin))
            return;

        ringRevealRemainingSeconds = configuration.ShakeRevealLifetimeSeconds;
        revealRingPosition = previous;
        hasRevealRingPosition = true;
        ResetShakeAttempt(mousePosition);
    }

    private bool IsShakeTriggered(Vector2 mousePosition, Vector2 origin)
    {
        var sensitivity = ShakeSensitivityScale;
        var netDistance = Vector2.Distance(mousePosition, origin);
        var pathToNetRatio = shakePathLength / Math.Max(netDistance, MinShakeNetDistance);
        var averageSpeed = shakePathLength / Math.Max(shakeWindowSeconds, 0.001f);

        return shakeMovementSamples >= RequiredShakeMovements
            && shakeDirectionChanges >= RequiredShakeDirectionChanges
            && shakePathLength >= RequiredShakePathLength / sensitivity
            && averageSpeed >= RequiredShakeAverageSpeed / sensitivity
            && pathToNetRatio >= RequiredShakePathToNetRatio;
    }

    private void ResetShakeAttempt(Vector2 mousePosition)
    {
        lastShakeMousePosition = mousePosition;
        shakeOriginPosition = mousePosition;
        lastShakeDirection = null;
        ResetShakeMetrics();
    }

    private void ResetShakeMetrics()
    {
        shakeWindowSeconds = 0f;
        shakePathLength = 0f;
        shakeDirectionChanges = 0;
        shakeMovementSamples = 0;
    }

    private void UpdateParticles(float deltaSeconds)
    {
        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var particle = particles[i];
            particle.RemainingSeconds -= deltaSeconds;

            if (particle.RemainingSeconds <= 0f)
            {
                particles.RemoveAt(i);
                continue;
            }

            particle.Position += particle.Velocity * deltaSeconds;
            particle.Velocity *= MathF.Pow(0.045f, deltaSeconds);
            particles[i] = particle;
        }
    }

    private void DrawParticles()
    {
        if (particles.Count == 0)
            return;

        var drawList = ImGui.GetForegroundDrawList();
        var color = ConfiguredColor();

        foreach (var particle in particles)
        {
            var t = Math.Clamp(particle.RemainingSeconds / particle.LifetimeSeconds, 0f, 1f);
            var alpha = Math.Clamp(color.W * t * t, 0f, 1f);
            var radius = particle.Size * (0.35f + 0.65f * t);
            var particleColor = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha));
            drawList.AddCircleFilled(particle.Position, radius, particleColor, 16);
        }
    }

    private void DrawCursorRing(Vector2 mousePosition, float now, float deltaSeconds)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var color = ConfiguredColor();
        var pulse = 1f + MathF.Sin(now * 5.5f) * 0.08f;
        var center = mousePosition;
        var radiusScale = 1f;

        if (IsRingRevealActive)
        {
            if (!hasRevealRingPosition)
            {
                revealRingPosition = mousePosition;
                hasRevealRingPosition = true;
            }

            var lifetime = Math.Max(configuration.ShakeRevealLifetimeSeconds, 0.2f);
            var remainingRatio = Math.Clamp(ringRevealRemainingSeconds / lifetime, 0f, 1f);
            var catchUp = 1f - MathF.Pow(0.000001f, deltaSeconds);
            revealRingPosition = Vector2.Lerp(revealRingPosition, mousePosition, catchUp);
            center = revealRingPosition;
            radiusScale = 1f + 2.35f * SmoothStep(remainingRatio);
        }

        var alpha = Math.Clamp(color.W, 0f, 1f);
        var ringColor = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha));
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, alpha * 0.45f));
        var radius = configuration.RingRadius * radiusScale * pulse;

        drawList.AddCircle(center, radius + 1.5f, shadowColor, 48, configuration.RingThickness + 1.5f);
        drawList.AddCircle(center, radius, ringColor, 48, configuration.RingThickness);
    }

    private Vector4 ConfiguredColor()
    {
        return new Vector4(
            configuration.ColorRed,
            configuration.ColorGreen,
            configuration.ColorBlue,
            configuration.ColorAlpha);
    }

    private float RandomSigned(float magnitude)
    {
        return RandomRange(-magnitude, magnitude);
    }

    private float RandomRange(float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }

    private bool CanShowRing => configuration.ShowCursorRing
        && (!configuration.OnlyShowRingDuringCombat || currentFrameInCombat);

    private bool CanRevealRing => CanShowRing && configuration.ShakeMouseToRevealRing;

    private bool IsRingRevealActive => CanRevealRing && ringRevealRemainingSeconds > 0f;

    private bool ShouldDrawRing => CanShowRing
        && (!configuration.ShakeMouseToRevealRing || IsRingRevealActive);

    private float ShakeSensitivityScale => Math.Clamp(1f + (configuration.ShakeSensitivity - 1f) * 0.25f, 0.8f, 1.5f);

    private const float ShakeRadius = 110f;
    private const float MinShakeStepDistance = 3f;
    private const float MaxShakeStepDistance = 52f;
    private const float ShakeWindowSeconds = 0.85f;
    private const float ShakeDirectionChangeDot = 0.35f;
    private const float RequiredShakePathLength = 150f;
    private const float RequiredShakeAverageSpeed = 230f;
    private const float RequiredShakePathToNetRatio = 2.2f;
    private const float MinShakeNetDistance = 18f;
    private const int RequiredShakeMovements = 5;
    private const int RequiredShakeDirectionChanges = 2;

    private static float SmoothStep(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private static bool IsAnyMouseButtonDown(ImGuiIOPtr io)
    {
        return IsWindowsMouseButtonDown(VkLButton)
            || IsWindowsMouseButtonDown(VkRButton)
            || IsWindowsMouseButtonDown(VkMButton)
            || IsWindowsMouseButtonDown(VkXButton1)
            || IsWindowsMouseButtonDown(VkXButton2)
            || IsAnyImGuiMouseButtonDown(io);
    }

    private static bool IsWindowsMouseButtonDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool IsAnyImGuiMouseButtonDown(ImGuiIOPtr io)
    {
        for (var i = 0; i < 5; i++)
        {
            if (io.MouseDown[i])
                return true;
        }

        return false;
    }

    private static bool IsInsideViewport(Vector2 position, Vector2 size)
    {
        return position.X >= 0
            && position.Y >= 0
            && position.X <= size.X
            && position.Y <= size.Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkXButton1 = 0x05;
    private const int VkXButton2 = 0x06;

    private struct Particle(Vector2 position, Vector2 velocity, float remainingSeconds, float lifetimeSeconds, float size)
    {
        public Vector2 Position = position;
        public Vector2 Velocity = velocity;
        public float RemainingSeconds = remainingSeconds;
        public readonly float LifetimeSeconds = lifetimeSeconds;
        public readonly float Size = size;
    }
}
