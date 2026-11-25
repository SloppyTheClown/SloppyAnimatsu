namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The preferences dialog's layout:
/// a flat, alphabetical list
/// of page titles - no headings and no nesting - where one row can show the members of more than one
/// preference page class (Rig Atelier shows <c>CSkeletonPreferences</c> and <c>CIKPreferences</c>) and
/// Decorators draws <c>CDisplayPreferences</c>, and each page is a curated set of hand-labelled rows.
/// The page contents are in <see cref="PreferenceRowSpec"/>; the measurement is
/// and preferences-and-shortcuts.md *The dialog in 2020-3-13*.
/// </summary>
public static class PreferenceCatalogue
{
    /// <summary>One row of the list: the title it shows, the page classes whose members it edits, and the curated rows.</summary>
    public sealed record Entry(
        string Title, IReadOnlyList<string> Classes, IReadOnlyList<PreferenceRowSpec>? Rows = null)
    {
        public bool Shows(string className)
        {
            foreach (string name in Classes)
                if (string.Equals(name, className, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>The 13 window-size modes the Anim Bank, Tree and Picker pages' combo draws, value 0 up.</summary>
    private static readonly PreferenceOption[] WindowSizes = BuildWindowSizes();

    private static PreferenceOption[] BuildWindowSizes()
    {
        var options = new List<PreferenceOption> { new("Standard", 0) };
        int value = 1;
        foreach (string prefix in new[] { "(Hovering) ", "(Frozen) " })
            foreach (string size in new[] { "x1.5", "x2", "x2.5", "x3", "x3.5", "x4" })
                options.Add(new PreferenceOption(prefix + size, value++));
        return options.ToArray();
    }

    // The combo boxes' option names and stored values
    // Time unit and One or two colors draw the larger value first. The Size unit
    // combo draws its seven options in value order - millimeter is 0 and meter 3, not 0.
    private static readonly PreferenceOption[] GradientColors =
        [new("Two colors with gradient", 1), new("Single color", 0)];

    private static readonly PreferenceOption[] JointDisplayModes =
        [new("Light", 0), new("Classic", 1)];

    private static readonly PreferenceOption[] RenderingQualities =
        [new("Fast", 0), new("Balanced", 1), new("Beautiful", 2)];

    private static readonly PreferenceOption[] SkinningAlgorithms =
        [new("Joint proximity", 0), new("Geodesic voxel", 1)];

    private static readonly PreferenceOption[] VoxelAccuracies =
        [new("Rough", 0), new("Balanced", 1), new("Accurate", 2)];

    private static readonly PreferenceOption[] GhostTypes =
        [new("Keys", 0), new("Frames", 1)];

    private static readonly PreferenceOption[] TimeUnits =
        [new("Frames", 1), new("Milliseconds", 0)];

    private static readonly PreferenceOption[] SizeUnits =
    [
        new("millimeter", 0), new("centimeter", 1), new("decimeter", 2), new("meter", 3),
        new("inch", 4), new("foot", 5), new("yard", 6),
    ];

    /// <summary>
    /// The rows 2020-3-13 lists, in the order it lists them (alphabetical by title), each with the rows
    /// its page draws in the order it draws them. Rig Atelier's merge of the skeleton and IK pages is
    /// curated, and so is Decorators showing <c>CDisplayPreferences</c>; every other row is the page its
    /// title names.
    /// </summary>
    public static IReadOnlyList<Entry> Curated { get; } =
    [
        new Entry("3D Rendering preferences", ["C3DRenderingPreferences"],
        [
            new("BackFace Cull", "BackFaceCull", "C3DRenderingPreferences", PreferenceRowWidget.Check),
        ]),
        new Entry("Anim Bank preferences", ["CAnimBankPreferences"],
        [
            new("Window size", "ResizementMode", "CAnimBankPreferences", PreferenceRowWidget.Combo, WindowSizes),
            new("Skip retargeting ref pose advice popup", "SkipRetargetingAlignRefPosesWindow",
                "CAnimBankPreferences", PreferenceRowWidget.Check),
        ]),
        new Entry("Autorigger preferences", ["CAutoRiggerPreferences"],
        [
            new("Root name", "RootName", "CAutoRiggerPreferences"),
            new("Hips name", "HipsName", "CAutoRiggerPreferences"),
            new("Spine name", "SpineName", "CAutoRiggerPreferences"),
            new("Chest name", "BeforeLastSpineName", "CAutoRiggerPreferences"),
            new("Upper chest name", "LastSpineName", "CAutoRiggerPreferences"),
            new("Neck name", "NeckName", "CAutoRiggerPreferences"),
            new("Head name", "HeadName", "CAutoRiggerPreferences"),
            new("Shoulder Name", "ShoulderName", "CAutoRiggerPreferences"),
            new("Upper arm name", "UpperArmName", "CAutoRiggerPreferences"),
            new("Lower arm name", "LowerArmName", "CAutoRiggerPreferences"),
            new("Hand name", "HandName", "CAutoRiggerPreferences"),
            new("Thumb finger name", "FingerThumbName", "CAutoRiggerPreferences"),
            new("Index finger name", "FingerIndexName", "CAutoRiggerPreferences"),
            new("Middle finger name", "FingerMiddleName", "CAutoRiggerPreferences"),
            new("Ring finger name", "FingerRingName", "CAutoRiggerPreferences"),
            new("Little finger name", "FingerLittleName", "CAutoRiggerPreferences"),
            new("Extra finger name", "FingerExtraName", "CAutoRiggerPreferences"),
            new("Upper leg name", "UpperLegName", "CAutoRiggerPreferences"),
            new("Lower leg name", "LowerLegName", "CAutoRiggerPreferences"),
            new("Foot name", "FootName", "CAutoRiggerPreferences", PreferenceRowWidget.Text, null, 3, false,
                null, null, null, "Drawn below the window edge at 1920x1040 (clipped in 2020-3-13)."),
            new("Toe name", "ToeName", "CAutoRiggerPreferences", PreferenceRowWidget.Text, null, 3, false,
                null, null, null, "Drawn below the window edge at 1920x1040 (clipped in 2020-3-13)."),
            new("Finger tip name", "FingerTipName", "CAutoRiggerPreferences", PreferenceRowWidget.Text, null, 3,
                false, null, null, null, "Drawn below the window edge at 1920x1040 (clipped in 2020-3-13)."),
        ]),
        new Entry("Color preferences", ["CColorsPreferences"],
        [
            new("3D Viewport (1st gradient color)", "FirstColorGradient", "CColorsPreferences",
                PreferenceRowWidget.Color),
            new("3D Viewport (2nd gradient color)", "SecondColorGradient", "CColorsPreferences",
                PreferenceRowWidget.Color),
            new("One or two colors", "SingleOrGradientColor", "CColorsPreferences", PreferenceRowWidget.Combo,
                GradientColors),
            new("Invert gradient color", "InverseColorGradient", "CColorsPreferences", PreferenceRowWidget.Check),
            new("Joints", "JointsColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Vertices", "VertexSelectedColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Vertices (last selected vertex)", "VertexLastSelectedColor", "CColorsPreferences",
                PreferenceRowWidget.Color),
            new("Vertices (copied)", "VertexSelectedForCopySkinColor", "CColorsPreferences",
                PreferenceRowWidget.Color),
            new("Vertices (last selected and copied vertex)", "VertexLastSelectedForCopySkinColor",
                "CColorsPreferences", PreferenceRowWidget.Color),
            new("Wireframe (front)", "WireframeFrontColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Wireframe (back)", "WireframeBackColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Grid (central lines)", "GridCentralLineColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Grid (edge lines)", "GridEdgeLinesColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Grid (subdivision lines)", "GridSubdivisionLinesColor", "CColorsPreferences",
                PreferenceRowWidget.Color),
            new("Next ghost color", "NextGhostsColor", "CColorsPreferences", PreferenceRowWidget.Color),
            new("Previous ghost color", "PreviousGhostsColor", "CColorsPreferences", PreferenceRowWidget.Color),
        ]),
        new Entry("Curveboard preferences", ["CCurveboardPreferences"],
        [
            new("All Curve Ahead", "DrawCurveFront", "CCurveboardPreferences", PreferenceRowWidget.Check),
        ]),
        new Entry("Data protection preferences", ["CDataProtectionPreferences"],
        [
            new("Send software analytics to improve software engineering", "GoogleAnalyticsUserConsent",
                "CDataProtectionPreferences", PreferenceRowWidget.Check),
            new("Send crash reports for troubleshooting", "CrashFolderUserConsent",
                "CDataProtectionPreferences", PreferenceRowWidget.Check),
            new("Read privacy policy online", "PrivacyPolicyHyperlink", "CDataProtectionPreferences",
                PreferenceRowWidget.Link, null, 3, false, null, null, null,
                "A hyperlink on the page; PrivacyPolicyHyperlink is a bool, so the row edits nothing."),
        ]),
        // The four size factors are CScene members too (every scene carries them; the new scene
        // writes 2/1/3/0.4, exactly what the page draws for a stored 0), so they bind to the open
        // scene like Rig Atelier's rows.
        new Entry("Decorators preferences", ["CDisplayPreferences"],
        [
            new("Decorator display affects all views", "ShowHideOnAllViews", "CDisplayPreferences",
                PreferenceRowWidget.Check),
            new("Skin decorator alpha", "SkinningZoneAlpha", "CDisplayPreferences", PreferenceRowWidget.Number),
            new("Blendshape picking decorator alpha", "BlendshapeInteractiveZoneAlpha", "CDisplayPreferences",
                PreferenceRowWidget.Number),
            new("Joint display mode", "JointDisplayMode", "CDisplayPreferences", PreferenceRowWidget.Combo,
                JointDisplayModes),
            new("Joint size factor (standard density area)", "JointRenderSize", "CDisplayPreferences",
                PreferenceRowWidget.Number, null, 3, false, 5.0, Scene: true), // new scenes: 5
            new("Joint size factor (high density area) - Light mode only", "JointRenderSizeHighDensity",
                "CDisplayPreferences", PreferenceRowWidget.Number, null, 3, false, 6.0, Scene: true),
            new("Pivot size factor", "PivotRenderSize", "CDisplayPreferences", PreferenceRowWidget.Number, null,
                3, false, 3.0, Scene: true),
            new("Pivot size factor (unselected joints)", "UnselectedPivotRenderSize", "CDisplayPreferences",
                PreferenceRowWidget.Number, null, 3, false, 0.4, Scene: true),
        ]),
        new Entry("General preferences", ["CGeneralPreferences"],
        [
            new("Rotate and zoom view on selection", "RotateAroundSelection", "CGeneralPreferences",
                PreferenceRowWidget.Check),
            new("Skip welcome window at startup", "SkipWelcomeWindow", "CGeneralPreferences",
                PreferenceRowWidget.Check),
            new("Do not show update window until next update", "SkipUpdateWindow", "CGeneralPreferences",
                PreferenceRowWidget.Check),
            new("Automatically check for updates at startup", "AutoCheckUpdates", "CGeneralPreferences",
                PreferenceRowWidget.Check),
            new("Autosaving files rate (minutes)", "AutoSaveTime", "CGeneralPreferences",
                PreferenceRowWidget.Number, null, 0),
            new("Undo/Redo max actions count", "UndoStackMaxSize", "CGeneralPreferences",
                PreferenceRowWidget.Number, null, 0),
            new("Reverse RMB Zoom View", "ReverseRMBZoomView", "CGeneralPreferences", PreferenceRowWidget.Check),
            new("Suppr button delete keyframes in Animation mode", "SupprButtonDeletekeyInAnimMode",
                "CGeneralPreferences", PreferenceRowWidget.Check),
            new("Deselect by single click in empty space", "DeselectByClickingEmptySpace", "CGeneralPreferences",
                PreferenceRowWidget.Check),
        ]),
        new Entry("Picker preferences", ["CPickerPreferences"],
        [
            new("Window size", "ResizementMode", "CPickerPreferences", PreferenceRowWidget.Combo, WindowSizes),
        ]),
        new Entry("Rendering preferences", ["CRenderingPreferences"],
        [
            new("Rendering quality vs. performances (requires restart)", "PerformancesUserChoice",
                "CRenderingPreferences", PreferenceRowWidget.Combo, RenderingQualities),
        ]),
        // The app draws all eight from the open scene, not from the profile
        // so they bind to CScene when one is open. The Default values are
        // The new-scene values (its CScene constructor, and SceneFactory), for a dialog with
        // no scene open. The snap threshold is in the set too: with the scene patched to
        // 9.5 the page drew 9.500, and an edit to 12.5 landed in CScene (the profile stayed 0).
        new Entry("Rig Atelier preferences", ["CSkeletonPreferences", "CIKPreferences"],
        [
            new("Auto-orient on joint creation", "Flags", "CSkeletonPreferences", PreferenceRowWidget.Check,
                null, 3, false, null, 4),
            new("Joint snap YZ plane pixel threshold (ortho views)", "SnapAxisPixelThreshold",
                "CSkeletonPreferences", PreferenceRowWidget.Number, null, 0),
            new("IK target scene size", "TargetRenderSize", "CIKPreferences", PreferenceRowWidget.Number, null,
                3, true, 0.08, Scene: true),
            new("RF bank angle max value", "BankAngleMax", "CIKPreferences", PreferenceRowWidget.Number, null,
                3, false, 60, Scene: true),
            new("RF twist angle max value", "TwistAngleMax", "CIKPreferences", PreferenceRowWidget.Number, null,
                3, false, 60, Scene: true),
            new("RF roll backward angle max value", "RollAngleBackMax", "CIKPreferences",
                PreferenceRowWidget.Number, null, 3, false, 60, Scene: true),
            new("RF roll angle bending limit value", "RollAnglePreMax", "CIKPreferences",
                PreferenceRowWidget.Number, null, 3, false, 60, Scene: true),
            new("RF roll angle unbending limit value", "RollAngleToesMax", "CIKPreferences",
                PreferenceRowWidget.Number, null, 3, false, 75, Scene: true),
            new("RF roll forward angle max value", "RollAngleFullMax", "CIKPreferences",
                PreferenceRowWidget.Number, null, 3, false, 85, Scene: true),
            new("RF zero value snap threshold (Roll, Bank, Twist)", "SnapAngleThreshold", "CIKPreferences",
                PreferenceRowWidget.Number, null, 3, false, 5.0, Scene: true),
        ]),
        new Entry("Skin Atelier preferences", ["CSkinWorkshopPreferences"],
        [
            new("Activate/Deactivate Weight step", "WeightStepOn", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Check),
            new("Weight step value", "WeightStep", "CSkinWorkshopPreferences", PreferenceRowWidget.Number, null,
                2),
            new("Activate/Deactivate Strength step", "StrengthStepOn", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Check),
            new("Strength step value", "StrengthStep", "CSkinWorkshopPreferences", PreferenceRowWidget.Number,
                null, 2),
            new("Sweep Weights value below", "PruneLimit", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Number, null, 2),
            new("Hide 3D manipulators if skin painter turned on", "HideManipWhenPaint",
                "CSkinWorkshopPreferences", PreferenceRowWidget.Check),
            new("Allow to mirror weights on different skinned meshes", "MirrorSkinOnDifferentMeshes",
                "CSkinWorkshopPreferences", PreferenceRowWidget.Check),
            new("Rigid skinning algorithm", "RigidSkinningAlgorithm", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Combo, SkinningAlgorithms),
            new("Voxel precision", "VoxelAccuracy", "CSkinWorkshopPreferences", PreferenceRowWidget.Combo,
                VoxelAccuracies),
            new("Max inf. pr vtx", "NbMaxJoints", "CSkinWorkshopPreferences", PreferenceRowWidget.Number, null,
                0),
            new("Low smooth strength (recommended for face)", "FaceIteration", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Number),
            new("High smooth strength (recommended for body)", "BodyIteration", "CSkinWorkshopPreferences",
                PreferenceRowWidget.Number),
            new("Negligible inf.", "AutoSweepValue", "CSkinWorkshopPreferences", PreferenceRowWidget.Number),
        ]),
        new Entry("Spinner preferences", ["CSpinnerPreferences"],
        [
            new("Discrete move step value", "MoveStep", "CSpinnerPreferences", PreferenceRowWidget.Number, null,
                3, true),
            new("Discrete rotate step value", "RotateStep", "CSpinnerPreferences", PreferenceRowWidget.Number),
            new("Discrete scale step value", "ScaleStep", "CSpinnerPreferences", PreferenceRowWidget.Number),
            new("Toon scale coef", "CompensatedScaleCoef", "CSpinnerPreferences", PreferenceRowWidget.Number),
            new("Show axis hints", null, "CSpinnerPreferences", PreferenceRowWidget.Check, null, 3, false, null,
                null, null, "Drawn by 2020-3-13 but not traced to a member (one of the page's flags)."),
            new("Use Move 3D gizmo in Spinner Rotate mode", "ComboMoveRotateMode", "CSpinnerPreferences",
                PreferenceRowWidget.Check),
            new("Synchronize the move and rotate modes coordinate system", "SyncMoveRotateModes",
                "CSpinnerPreferences", PreferenceRowWidget.Check),
        ]),
        new Entry("Stacker preferences", ["CStackerPreferences"],
        [
            new("Ghosts on playing", "Flags", "CStackerPreferences", PreferenceRowWidget.Check, null, 3, false,
                null, 1),
            new("Ghosts always wire", "Flags", "CStackerPreferences", PreferenceRowWidget.Check, null, 3, false,
                null, 2, "Ghosts on playing"),
            new("Prev. ghosts", "NbPreviousGhosts", "CStackerPreferences", PreferenceRowWidget.Number, null, 0),
            new("Next ghosts", "NbNextGhosts", "CStackerPreferences", PreferenceRowWidget.Number, null, 0),
            new("Ghosts on", "GhostsType", "CStackerPreferences", PreferenceRowWidget.Combo, GhostTypes),
            new("Increase decrease timing via hotkey (ms)", "IncreaseDecreaseTiming", "CStackerPreferences",
                PreferenceRowWidget.Number, null, 0),
            new("Increase decrease timing via hotkey (frame)", "IncreaseDecreaseFrame", "CStackerPreferences",
                PreferenceRowWidget.Number, null, 0),
        ]),
        new Entry("Timebar preferences", ["CTimelinePreferences"],
        [
            new("Unified timelines", "UnifiedTimeline", "CTimelinePreferences", PreferenceRowWidget.Check),
            new("Displayed stacks", "NbVisibleStacks", "CTimelinePreferences", PreferenceRowWidget.Number, null,
                0),
            new("Time unit", "TimeUnit", "CTimelinePreferences", PreferenceRowWidget.Combo, TimeUnits),
            new("Scene Frame Rate", null, "CTimelinePreferences", PreferenceRowWidget.Number, null, 0, false,
                null, null, null, "Follows the open scene's frame rate, not the profile."),
            new("Play speed", "PlaySpeed", "CTimelinePreferences", PreferenceRowWidget.Number),
            new("Audio playback scrub length", "AudioScrubDuration", "CTimelinePreferences",
                PreferenceRowWidget.Number, null, 0),
        ]),
        new Entry("Transform preferences", ["CTransformPreferences", Preferences.TransformPageClass],
        [
            // SloppyAnimatsu's own row (CMyAkTransformPreferences): the TRANSFORM window's
            // translation unit. Scene units are centimetres; metres divide them by 100.
            new("Translation unit", Preferences.TranslationUnitMember, Preferences.TransformPageClass,
                PreferenceRowWidget.Combo,
                [new("Centimeters (default)", (int)TranslationUnit.SceneUnits),
                 new("Meters (scene units / 100)", (int)TranslationUnit.Meters)]),
            new("Copy/Paste IK target color", "CopyPasteIKColor", "CTransformPreferences",
                PreferenceRowWidget.Check),
            new("Mirror IK target color", "MirrorIKColor", "CTransformPreferences", PreferenceRowWidget.Check),
        ]),
        new Entry("Tree preferences", ["CHierarchyPreferences"],
        [
            new("Window size", "ResizementMode", "CHierarchyPreferences", PreferenceRowWidget.Combo, WindowSizes),
        ]),
        // The app draws all three from the open scene, not from the profile
        // so they bind to CScene when one is open. The defaults are the
        // new-scene values (GridSize 4, UnitSubdivision 5), for a dialog with no scene open; the probe
        // scene's grid size of 10 that an earlier note used as a default is not the tool default.
        new Entry("Unit & grid preferences", ["CUnitAndGridPreferences"],
        [
            new("Size unit", "SizeUnit", "CUnitAndGridPreferences", PreferenceRowWidget.Combo, SizeUnits,
                Scene: true),
            new("Grid size", "GridSize", "CUnitAndGridPreferences", PreferenceRowWidget.Number, null, 0, false,
                4, Scene: true),
            new("Unit subdivisions", "UnitSubdivision", "CUnitAndGridPreferences", PreferenceRowWidget.Number,
                null, 0, false, 5, Scene: true),
        ]),
    ];

    // Preference pages the dialog does not list still get a row, appended after the curated
    // ones, so their values stay reachable.
    /// <summary>
    /// SloppyAnimatsu's own pages, listed after the curated ones.
    /// </summary>
    public static IReadOnlyList<Entry> Own { get; } =
    [
        new Entry("Material preferences", [Preferences.MaterialPageClass],
        [
            new("Show the Material panel without a selected mesh", Preferences.MaterialAlwaysShow,
                Preferences.MaterialPageClass, PreferenceRowWidget.Check),
            new("Pixel filtering (nearest) for added textures", Preferences.MaterialNearestForNew,
                Preferences.MaterialPageClass, PreferenceRowWidget.Check),
            new("Preview size (pixels)", Preferences.MaterialPreviewSize, Preferences.MaterialPageClass,
                PreferenceRowWidget.Number, null, 0, false, Preferences.DefaultMaterialPreviewSize),
        ]),
        new Entry("Anim Bank preferences", [Preferences.AnimBankPageClass],
        [
            new("Sort", Preferences.AnimBankSortMember, Preferences.AnimBankPageClass, PreferenceRowWidget.Combo,
                [new("A-Z", 0), new("Free (manual order)", 1), new("Added", 2)]),
            new("Reversed (Z-A; oldest first)", Preferences.AnimBankReversedMember,
                Preferences.AnimBankPageClass, PreferenceRowWidget.Check),
        ]),
    ];

    private static readonly Dictionary<string, string> UnlistedTitles = new(StringComparer.Ordinal)
    {
        ["CExportPreferences"] = "FBX Export preferences",
        ["CFacePreferences"] = "Face preferences",
        ["CMirrorPreferences"] = "Mirror preferences",
        ["CSkinningPreferences"] = "Skinning preferences",
    };

    /// <summary>
    /// Page classes the dialog never lists. <c>CDataProtectionPreferences</c> holds analytics and
    /// crash-report consent boxes; SloppyAnimatsu collects and sends no data at all, so showing the page
    /// would wrongly suggest it does. Its values stay in the file untouched (and are still written back
    /// for akeytsu); the curated rows are kept so the page can come back by dropping it from this set.
    /// </summary>
    public static IReadOnlySet<string> HiddenClasses { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "CDataProtectionPreferences" };

    /// <summary>True for a page class the dialog leaves out (<see cref="HiddenClasses"/>).</summary>
    public static bool IsHidden(string className) => HiddenClasses.Contains(className);

    /// <summary>
    /// The rows the dialog shows for a file: the curated entries whose classes the file has, in the
    /// catalogue order, then a row for every page the catalogue does not cover, so no value of the
    /// file becomes unreachable. An entry keeps its curated rows when one of its classes is missing.
    /// </summary>
    public static IReadOnlyList<Entry> For(Preferences preferences)
    {
        var present = new List<string>();
        foreach (var page in preferences.Pages)
            if (!IsHidden(page.ClassName)) present.Add(page.ClassName);

        var entries = new List<Entry>();
        foreach (var entry in Curated.Concat(Own))
        {
            var classes = new List<string>();
            foreach (string className in entry.Classes)
                if (present.Contains(className, StringComparer.Ordinal)) classes.Add(className);
            if (classes.Count > 0) entries.Add(new Entry(entry.Title, classes, entry.Rows));
        }
        foreach (string className in present)
        {
            bool covered = false;
            foreach (var entry in entries)
                if (entry.Shows(className)) { covered = true; break; }
            if (!covered) entries.Add(new Entry(TitleFor(className), [className]));
        }
        return entries;
    }

    /// <summary>The title a page class is listed under (<c>CGeneralPreferences</c> is "General preferences").</summary>
    public static string TitleFor(string className)
    {
        foreach (var entry in Curated.Concat(Own))
            if (entry.Shows(className)) return entry.Title;
        if (UnlistedTitles.TryGetValue(className, out string? known)) return known;
        string name = className.StartsWith('C') ? className[1..] : className;
        if (name.EndsWith("Preferences", StringComparison.Ordinal)) name = name[..^"Preferences".Length];
        return name.Length == 0 ? className : name + " preferences";
    }
}
