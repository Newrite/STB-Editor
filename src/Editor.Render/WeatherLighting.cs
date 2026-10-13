using System.Numerics;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Render;

/// <summary>When the sky turns: the climate's sunrise and sunset (hours), Skyrim's defaults without a climate.</summary>
public readonly record struct DayTimes(float SunriseBegin, float SunriseEnd, float SunsetBegin, float SunsetEnd)
{
    public static readonly DayTimes Default = new(5.33f, 9f, 17f, 20.5f);

    public static DayTimes Of(IClimateGetter? climate) => climate is null ? Default
        : new DayTimes(Hours(climate.SunriseBegin), Hours(climate.SunriseEnd), Hours(climate.SunsetBegin), Hours(climate.SunsetEnd));

    private static float Hours(TimeOnly t) => t.Hour + t.Minute / 60f;
}

/// <summary>
/// An outdoor view lit as a weather (WTHR) at an hour (CK: the sky and time of day of the render window): the weather's four
/// colour sets — sunrise, day, sunset, night — blended by the climate's times (night until sunrise begins, the sunrise
/// colours in the middle of it, day from its end to sunset's beginning, sunset in its middle, night after it); sunlight
/// colour and the sun's (or at night the moon's) direction on an east–south–west arc; ambient from the directional ambient
/// colours (mostly the one from above); fog colour and distances; the sky colour behind everything from the horizon and
/// the upper sky.
/// </summary>
public static class WeatherLighting
{
    public static (SceneLighting Lighting, Vector4 Sky) Compute(IWeatherGetter weather, float hour, DayTimes times)
    {
        var (w, day) = Weights(hour, times);
        Vector3 Blend(IWeatherColorGetter? c) => c is null ? Vector3.Zero
            : (C(c.Sunrise) * w.X + C(c.Day) * w.Y + C(c.Sunset) * w.Z + C(c.Night) * w.W);

        var ambientSets = weather.DirectionalAmbientLightingColors;
        Vector3 Ambient(IAmbientColorsGetter? a) => a is null ? Vector3.Zero
            : C(a.DirectionalZPlus) * 0.5f + (C(a.DirectionalXPlus) + C(a.DirectionalXMinus) + C(a.DirectionalYPlus) + C(a.DirectionalYMinus)) * 0.1f + C(a.DirectionalZMinus) * 0.1f;
        var ambient = ambientSets is null ? Blend(weather.AmbientColor)
            : Ambient(ambientSets.Sunrise) * w.X + Ambient(ambientSets.Day) * w.Y + Ambient(ambientSets.Sunset) * w.Z + Ambient(ambientSets.Night) * w.W;

        var sun = Blend(weather.SunlightColor);
        var fogNear = Lerp(weather.FogDistanceNightNear, weather.FogDistanceDayNear, day);
        var fogFar = Lerp(weather.FogDistanceNightFar, weather.FogDistanceDayFar, day);
        var fogColour = Vector3.Lerp(Blend(weather.FogNearColor), Blend(weather.FogFarColor), 0.5f);
        var sky = Vector3.Lerp(Blend(weather.HorizonColor), Blend(weather.SkyUpperColor), 0.35f);
        var lighting = new SceneLighting(ambient, sun, SunDirection(hour, times), fogColour, Math.Max(0, fogNear), Math.Max(fogNear + 1, fogFar));
        return (lighting, new Vector4(sky, 1));
    }

    /// <summary>Weights of the sunrise, day, sunset and night colours at an hour, and how much of it is day (fog distances).</summary>
    public static (Vector4 Weights, float Day) Weights(float hour, DayTimes t)
    {
        hour = ((hour % 24) + 24) % 24;
        var sunriseMid = (t.SunriseBegin + t.SunriseEnd) / 2;
        var sunsetMid = (t.SunsetBegin + t.SunsetEnd) / 2;
        // Key points: night → sunrise → day … day → sunset → night.
        (float Hour, Vector4 W)[] keys =
        [
            (t.SunriseBegin, new(0, 0, 0, 1)),
            (sunriseMid, new(1, 0, 0, 0)),
            (t.SunriseEnd, new(0, 1, 0, 0)),
            (t.SunsetBegin, new(0, 1, 0, 0)),
            (sunsetMid, new(0, 0, 1, 0)),
            (t.SunsetEnd, new(0, 0, 0, 1)),
        ];
        Vector4 weights;
        if (hour <= keys[0].Hour || hour >= keys[^1].Hour) weights = new(0, 0, 0, 1);
        else
        {
            weights = keys[^1].W;
            for (var i = 0; i + 1 < keys.Length; i++)
                if (hour >= keys[i].Hour && hour <= keys[i + 1].Hour)
                {
                    var span = keys[i + 1].Hour - keys[i].Hour;
                    weights = Vector4.Lerp(keys[i].W, keys[i + 1].W, span > 0 ? (hour - keys[i].Hour) / span : 1);
                    break;
                }
        }
        return (weights, Math.Clamp(1 - weights.W, 0, 1));
    }

    /// <summary>Towards the sun by day (rising in the east, +X, high in the south, setting in the west), towards the moon by night.</summary>
    public static Vector3 SunDirection(float hour, DayTimes t)
    {
        hour = ((hour % 24) + 24) % 24;
        // The sun is up from the beginning of sunrise to the end of sunset.
        var rise = t.SunriseBegin;
        var set = t.SunsetEnd;
        float angle;
        if (hour >= rise && hour <= set) angle = MathF.PI * (hour - rise) / Math.Max(0.1f, set - rise);
        else
        {
            // The moon crosses the night sky the same way.
            var night = 24 - (set - rise);
            var since = hour > set ? hour - set : hour + 24 - set;
            angle = MathF.PI * since / Math.Max(0.1f, night);
        }
        // Never quite on the horizon: a light from below the ground would light nothing.
        var height = MathF.Max(0.12f, MathF.Sin(angle));
        return Vector3.Normalize(new Vector3(MathF.Cos(angle), -0.45f * height, height));
    }

    private static Vector3 C(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
