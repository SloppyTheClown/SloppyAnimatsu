namespace SloppyAnimatsu.Rendering;

/// <summary>
/// The Spinner's discrete (snap) steps
/// (discrete steps)"): per transform kind, whether a drag snaps and the step it snaps to, with
/// The shipped preference defaults (move 0.1, rotate 45 degrees, scale 0.5). Snapping
/// applies to the current object of a drag only: its snapped change is what the other selected
/// objects undergo.
/// </summary>
public sealed class SnapSettings
{
    /// <summary>Snap translation to a multiple of <see cref="MoveStep"/>.</summary>
    public bool MoveDiscrete { get; set; }

    /// <summary>The translation step, in scene units.</summary>
    public float MoveStep { get; set; } = 0.1f;

    /// <summary>Snap the turned angle to a multiple of <see cref="RotateStep"/>.</summary>
    public bool RotateDiscrete { get; set; }

    /// <summary>The rotation step, in degrees.</summary>
    public float RotateStep { get; set; } = 45f;

    /// <summary>Snap scale components to a multiple of <see cref="ScaleStep"/>.</summary>
    public bool ScaleDiscrete { get; set; }

    /// <summary>The scale step.</summary>
    public float ScaleStep { get; set; } = 0.5f;

    /// <summary>
    /// Also snap the turned angle to the nearest multiple of 90 degrees when it is within this
    /// many degrees of it (0 = off). The.akt scene threshold defaults to 5 degrees.
    /// </summary>
    public float AngleThreshold { get; set; }

    /// <summary>Whether any snapping is on.</summary>
    public bool Any => MoveDiscrete || RotateDiscrete || ScaleDiscrete || AngleThreshold > 0;

    /// <summary>
    /// The rotation step applied to a turned angle (radians): the discrete rotation step, then the
    /// right-angle threshold.
    /// </summary>
    public float SnapAngle(float radians)
    {
        if (RotateDiscrete && RotateStep > 0)
            radians = ToStep(radians, RotateStep * MathF.PI / 180);
        if (AngleThreshold > 0)
        {
            const float right = MathF.PI / 2;
            float nearest = ToStep(radians, right);
            if (MathF.Abs(radians - nearest) <= AngleThreshold * MathF.PI / 180)
                radians = nearest;
        }
        return radians;
    }

    /// <summary>The nearest multiple of <see cref="MoveStep"/> to a translation coordinate.</summary>
    public float SnapMove(float value) => MoveDiscrete && MoveStep > 0 ? ToStep(value, MoveStep) : value;

    /// <summary>The nearest multiple of <see cref="ScaleStep"/> to a scale component.</summary>
    public float SnapScale(float value) => ScaleDiscrete && ScaleStep > 0 ? ToStep(value, ScaleStep) : value;

    private static float ToStep(float value, float step) =>
        step > 0 ? MathF.Round(value / step, MidpointRounding.AwayFromZero) * step : value;
}
