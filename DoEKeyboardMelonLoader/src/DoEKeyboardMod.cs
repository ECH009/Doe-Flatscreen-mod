using System;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DoEKeyboard.DoEKeyboardMod), "DoE Keyboard Test", "0.4.7", "Echo9")]
[assembly: MelonGame(null, "Dungeons of Eternity")]

namespace DoEKeyboard;

public sealed class DoEKeyboardMod : MelonMod
{
    private const int VK_MBUTTON = 0x04;
    private const int VK_W = 0x57;
    private const int VK_S = 0x53;
    private const int VK_A = 0x41;
    private const int VK_D = 0x44;

    // Verified IL2CPP object offsets from the game's dump.cs.
    // VRControllerProps:
    //   propRootLeft  +0x218
    //   propRootRight +0x220
    private const int PROP_ROOT_LEFT_OFFSET = 0x218;
    private const int PROP_ROOT_RIGHT_OFFSET = 0x220;

    // PropRoot:
    //   <prop>k__BackingField +0x38
    // This is the currently held Prop for that hand.
    private const int PROP_ROOT_PROP_OFFSET = 0x38;

    // Prop (TypeDefIndex 306):
    //   type +0x50
    private const int PROP_TYPE_OFFSET = 0x50;

    // Prop -> DynamicAnchor -> Rigidbody native offsets.
    // Prop.dynamicAnchor backing field is +0x1A8.
    // DynamicAnchorBase.r is +0x38.
    private const int PROP_DYNAMIC_ANCHOR_OFFSET = 0x1A8;
    private const int DYNAMIC_ANCHOR_RIGIDBODY_OFFSET = 0x38;

    // Scripted melee state. Each mouse press starts one deterministic cut.
    // LMB: vertical cut (upward windup -> downward strike).
    // RMB: horizontal cut (left windup -> right strike).
    private static bool _leftSwingActive;
    private static bool _rightSwingActive;
    private static bool _leftAttackHeld;
    private static bool _rightAttackHeld;
    private static float _leftSwingStartTime;
    private static float _rightSwingStartTime;
    private static Quaternion _leftSwingStartRotation = Quaternion.identity;
    private static Quaternion _rightSwingStartRotation = Quaternion.identity;
    private static bool _leftSwingRotationInitialized;
    private static bool _rightSwingRotationInitialized;

    private const float SWING_WINDUP_TIME = 0.0f;
    private const float SWING_STRIKE_TIME = 0.20f;
    private const float SWING_TOTAL_TIME = SWING_WINDUP_TIME + SWING_STRIKE_TIME;
    private const float SWING_WINDUP_SPEED = 5.5f;
    private const float SWING_STRIKE_SPEED = 20.0f;
    private const float SWING_WINDUP_ANGULAR = 7.0f;
    private const float SWING_STRIKE_ANGULAR = 28.0f;

    // Total rotational travel of the weapon. The weapon first winds up,
    // then rotates through the strike as an actual angular arc rather than
    // merely translating linearly.
    private const float SWING_WINDUP_ANGLE = 70.0f;
    private const float SWING_STRIKE_ANGLE = 110.0f;
    // Small forward shove during the attack, applied along the weapon's initial local forward direction.
    private const float SWING_FORWARD_SPEED = 80.0f;

    private static HarmonyLib.Harmony? _harmony;
    private static MethodInfo? _addExternalMoveStickInput;

    // Cached generated IL2CPP proxy types. These are resolved once during
    // initialization rather than from the per-frame monitoring path.
    private static Type? _propRootType;
    private static Type? _propType;
    private static Type? _weaponType;
    private static Type? _weaponMeleeType;
    private static Type? _shieldType;

    // Native object pointer of the last Prop observed in each hand.
    // Comparing pointers lets us detect both pickup and drop without logging
    // every frame.
    private static IntPtr _lastLeftHeldPropPtr;
    private static IntPtr _lastRightHeldPropPtr;
    private static bool _heldPropMonitorSawValidHands;

    // WeaponMelee detection note:
    // The held Prop is intentionally wrapped as the base Prop class for diagnostics.
    // That means CLR IsInstanceOfType(WeaponMelee, prop) will NOT identify a derived
    // IL2CPP object. We therefore use Prop.type as the authoritative fallback and
    // separately create a WeaponMelee proxy when the type says it is melee.
    private static object? _leftWeaponMelee;
    private static object? _rightWeaponMelee;

    private static bool _loggedInput;
    private static bool _loggedA;

    public override void OnInitializeMelon()
    {
        MelonLogger.Msg("DoE Keyboard 0.4.6 loading...");

        try
        {
            _harmony = new HarmonyLib.Harmony("doe.keyboard.prototype");

            PatchMovement();
            PatchQuestA();
            PatchHeldPropMonitor();

            MelonLogger.Msg("DoE keyboard controls initialized.YIPPEE");
            MelonLogger.Msg("WASD = movement IS A GO");
            MelonLogger.Msg("0.4.7 LMB/RMB = attack");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"NO Patch failed: {ex}");
        }
    }

    public override void OnDeinitializeMelon()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;

        _addExternalMoveStickInput = null;
        _propRootType = null;
        _propType = null;
        _weaponType = null;
        _weaponMeleeType = null;
        _shieldType = null;

        _lastLeftHeldPropPtr = IntPtr.Zero;
        _lastRightHeldPropPtr = IntPtr.Zero;
        _heldPropMonitorSawValidHands = false;
        _lastHandsPointer = IntPtr.Zero;
        _leftSwingActive = _rightSwingActive = false;
        _leftAttackHeld = _rightAttackHeld = false;
        _leftSwingStartTime = _rightSwingStartTime = 0f;
        _leftSwingStartRotation = _rightSwingStartRotation = Quaternion.identity;
        _leftSwingRotationInitialized = _rightSwingRotationInitialized = false;
        _leftWeaponMelee = null;
        _rightWeaponMelee = null;
    }

    private void PatchMovement()
    {
        Type? controllerType = HarmonyLib.AccessTools.TypeByName("VRControllerInput");
        if (controllerType == null)
        {
            MelonLogger.Error("Could not find IL2CPP type VRControllerInput.");
            return;
        }

        MethodInfo? update = HarmonyLib.AccessTools.Method(controllerType, "Update");
        _addExternalMoveStickInput = HarmonyLib.AccessTools.Method(
            controllerType,
            "AddExternalMoveStickInput",
            new[] { typeof(Vector2) });

        if (update == null || _addExternalMoveStickInput == null)
        {
            MelonLogger.Error("Could not find VRControllerInput movement methods.");
            return;
        }

        MethodInfo prefix = typeof(DoEKeyboardMod).GetMethod(
            nameof(UpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic)!;

        _harmony!.Patch(update, prefix: new HarmonyLib.HarmonyMethod(prefix));
        MelonLogger.Msg("Patched VRControllerInput.Update().");
    }

    private void PatchQuestA()
    {
        int patched = 0;

        foreach (string typeName in new[] { "OculusInput", "FallbackInput" })
        {
            Type? inputType = HarmonyLib.AccessTools.TypeByName(typeName);
            if (inputType == null)
                continue;

            patched += PatchBooleanGetter(inputType, "A", nameof(AButtonPrefix));
            patched += PatchBooleanGetter(inputType, "ADown", nameof(AButtonDownPrefix));
            patched += PatchBooleanGetter(inputType, "AUp", nameof(AButtonUpPrefix));
        }

        Type? xrType = HarmonyLib.AccessTools.TypeByName("XRInput");
        if (xrType != null)
        {
            MethodInfo? getButton = HarmonyLib.AccessTools.Method(xrType, "GetButton");
            MethodInfo? getButtonDown = HarmonyLib.AccessTools.Method(xrType, "GetButtonDown");

            if (getButton != null)
            {
                MethodInfo prefix = typeof(DoEKeyboardMod).GetMethod(
                    nameof(GetButtonPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
                _harmony!.Patch(getButton, prefix: new HarmonyLib.HarmonyMethod(prefix));
                patched++;
            }

            if (getButtonDown != null)
            {
                MethodInfo prefix = typeof(DoEKeyboardMod).GetMethod(
                    nameof(GetButtonDownPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
                _harmony!.Patch(getButtonDown, prefix: new HarmonyLib.HarmonyMethod(prefix));
                patched++;
            }
        }

        if (patched == 0)
            MelonLogger.Warning("Could not find any A-button input methods; middle mouse A binding was not installed.");
        else
            MelonLogger.Msg($"Middle Mouse -> Quest A binding installed ({patched} hooks).");
    }

    private static int PatchBooleanGetter(Type type, string propertyName, string prefixName)
    {
        MethodInfo? getter = HarmonyLib.AccessTools.PropertyGetter(type, propertyName);
        if (getter == null)
            return 0;

        MethodInfo prefix = typeof(DoEKeyboardMod).GetMethod(
            prefixName, BindingFlags.Static | BindingFlags.NonPublic)!;
        _harmony!.Patch(getter, prefix: new HarmonyLib.HarmonyMethod(prefix));
        return 1;
    }

    private void PatchHeldPropMonitor()
    {
        Type? handsType = HarmonyLib.AccessTools.TypeByName("VRControllerHands");
        _propRootType = HarmonyLib.AccessTools.TypeByName("PropRoot");
        _propType = HarmonyLib.AccessTools.TypeByName("Prop");
        _weaponType = HarmonyLib.AccessTools.TypeByName("Weapon");
        _weaponMeleeType = HarmonyLib.AccessTools.TypeByName("WeaponMelee");
        _shieldType = HarmonyLib.AccessTools.TypeByName("Shield");

        if (handsType == null)
        {
            MelonLogger.Warning("VRControllerHands was not found; held Prop monitoring was not installed.");
            return;
        }

        MelonLogger.Msg("========== 0.4.1 held Prop stats ==========");
        MelonLogger.Msg($"VRControllerHands: {FormatType(handsType)}");
        MelonLogger.Msg($"PropRoot: {FormatType(_propRootType)}");
        MelonLogger.Msg($"Prop: {FormatType(_propType)}");
        MelonLogger.Msg($"Weapon: {FormatType(_weaponType)}");
        MelonLogger.Msg($"is it WeaponMelee: {FormatType(_weaponMeleeType)}");
        MelonLogger.Msg($"is it Shield: {FormatType(_shieldType)}");
        MelonLogger.Msg(
            $"Native offsets: hands.propRootLeft=+0x{PROP_ROOT_LEFT_OFFSET:X}, " +
            $"hands.propRootRight=+0x{PROP_ROOT_RIGHT_OFFSET:X}, " +
            $"PropRoot.prop=+0x{PROP_ROOT_PROP_OFFSET:X}, Prop.type=+0x{PROP_TYPE_OFFSET:X}");

        MethodInfo? update = HarmonyLib.AccessTools.Method(handsType, "Update");
        if (update == null)
        {
            MelonLogger.Warning("VRControllerHands.Update was not found; NOOOOOOO");
            return;
        }

        MethodInfo postfix = typeof(DoEKeyboardMod).GetMethod(
            nameof(HeldPropMonitorPostfix), BindingFlags.Static | BindingFlags.NonPublic)!;

        _harmony!.Patch(update, postfix: new HarmonyLib.HarmonyMethod(postfix));

        Type? dynamicAnchorType = HarmonyLib.AccessTools.TypeByName("DynamicAnchorBase");
        MethodInfo? physicsUpdate = dynamicAnchorType == null
            ? null
            : HarmonyLib.AccessTools.Method(dynamicAnchorType, "UpdatePhysics");

        if (physicsUpdate != null)
        {
            MethodInfo physicsPostfix = typeof(DoEKeyboardMod).GetMethod(
                nameof(DynamicAnchorPhysicsPostfix), BindingFlags.Static | BindingFlags.NonPublic)!;
            _harmony!.Patch(physicsUpdate, postfix: new HarmonyLib.HarmonyMethod(physicsPostfix));
            MelonLogger.Msg("Patched DynamicAnchorBase.UpdatePhysics() for synthetic melee physics.");
        }
        else
        {
            MelonLogger.Warning("DynamicAnchorBase.UpdatePhysics() was not found; synthetic melee physics was not installed.");
        }

        MelonLogger.Msg("Patched VRControllerHands.Update() for held Prop monitoring.");
       
    }

    private static void HeldPropMonitorPostfix(object __instance)
    {
        if (__instance == null)
            return;

        try
        {
            IntPtr handsPtr = GetNativePointer(__instance);
            if (handsPtr == IntPtr.Zero)
                return;

            _lastHandsPointer = handsPtr;
            MonitorHeldProp(__instance, handsPtr, true);
            MonitorHeldProp(__instance, handsPtr, false);
            CaptureMouseSwing(handsPtr, true);
            CaptureMouseSwing(handsPtr, false);
            _heldPropMonitorSawValidHands = true;
        }
        catch (Exception ex)
        {
            // Diagnostics must never interfere with the game's hand update.
            if (!_heldPropMonitorSawValidHands)
                MelonLogger.Warning($"Held Prop monitor error: {ex.Message}");
        }
    }

    private static void MonitorHeldProp(object hands, IntPtr handsPtr, bool isLeft)
    {
        int rootOffset = isLeft ? PROP_ROOT_LEFT_OFFSET : PROP_ROOT_RIGHT_OFFSET;
        IntPtr propRootPtr = ReadObjectPointer(handsPtr, rootOffset);

        if (propRootPtr == IntPtr.Zero)
            return;

        // PropRoot.<prop>k__BackingField is at +0x38 in the game dump.
        IntPtr propPtr = ReadObjectPointer(propRootPtr, PROP_ROOT_PROP_OFFSET);

        if (isLeft)
        {
            if (propPtr == _lastLeftHeldPropPtr)
                return;

            IntPtr previous = _lastLeftHeldPropPtr;
            _lastLeftHeldPropPtr = propPtr;
            LogHeldPropChange("LEFT", propPtr, previous, propRootPtr);
        }
        else
        {
            if (propPtr == _lastRightHeldPropPtr)
                return;

            IntPtr previous = _lastRightHeldPropPtr;
            _lastRightHeldPropPtr = propPtr;
            LogHeldPropChange("RIGHT", propPtr, previous, propRootPtr);
        }
    }

    private static void LogHeldPropChange(
        string side,
        IntPtr propPtr,
        IntPtr previousPtr,
        IntPtr propRootPtr)
    {
        if (propPtr == IntPtr.Zero)
        {
            if (side == "LEFT") _leftWeaponMelee = null;
            else _rightWeaponMelee = null;

            MelonLogger.Msg(
                $"[HELD PROP] {side}: UNEQUIPPED | " +
                $"previous=0x{previousPtr.ToInt64():X} | " +
                $"PropRoot=0x{propRootPtr.ToInt64():X}");
            return;
        }

        int propTypeValue = int.MinValue;
        try
        {
            propTypeValue = Marshal.ReadInt32(IntPtr.Add(propPtr, PROP_TYPE_OFFSET));
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[HELD PROP] {side}: could not read Prop.type: {ex.Message}");
        }

        object? prop = null;
        try
        {
            prop = WrapIl2CppObject(_propType, propPtr);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[HELD PROP] {side}: could not wrap Prop: {ex.Message}");
        }

        string objectName = "<unknown>";
        string objectToString = "<unknown>";
        string runtimeType = "<wrapper unavailable>";

        if (prop != null)
        {
            runtimeType = prop.GetType().FullName ?? prop.GetType().Name;

            try
            {
                if (prop is UnityEngine.Object unityObject)
                {
                    objectName = unityObject.name;
                    objectToString = unityObject.ToString();
                }
                else
                {
                    objectToString = prop.ToString() ?? "<null>";
                }
            }
            catch (Exception ex)
            {
                objectName = $"<error: {ex.Message}>";
            }
        }

        string typeName = propTypeValue == int.MinValue
            ? "<read failed>"
            : GetPropTypeName(propTypeValue);

        bool isWeapon = _weaponType != null && prop != null && _weaponType.IsInstanceOfType(prop);
        bool clrIsMelee = _weaponMeleeType != null && prop != null && _weaponMeleeType.IsInstanceOfType(prop);
        bool isMelee = IsMeleePropType(propTypeValue) || clrIsMelee;
        bool isShield = _shieldType != null && prop != null && _shieldType.IsInstanceOfType(prop);

        // The base Prop wrapper above cannot reliably expose the derived CLR type.
        // When Prop.type identifies a melee weapon, wrap the SAME native pointer as
        // WeaponMelee. This gives later code a real WeaponMelee proxy to invoke.
        object? meleeProxy = null;
        if (isMelee && _weaponMeleeType != null)
        {
            try
            {
                meleeProxy = WrapIl2CppObject(_weaponMeleeType, propPtr);
                if (side == "LEFT") _leftWeaponMelee = meleeProxy;
                else _rightWeaponMelee = meleeProxy;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[HELD PROP] {side}: could not create WeaponMelee proxy: {ex.Message}");
            }
        }

        MelonLogger.Msg($"[HELD PROP] {side}: EQUIPPED/CHANGED");
        MelonLogger.Msg($"  Prop pointer:       0x{propPtr.ToInt64():X}");
        MelonLogger.Msg($"  Previous pointer:   0x{previousPtr.ToInt64():X}");
        MelonLogger.Msg($"  PropRoot pointer:   0x{propRootPtr.ToInt64():X}");
        MelonLogger.Msg($"  Unity name:         {objectName}");
        MelonLogger.Msg($"  Runtime type:       {runtimeType}");
        MelonLogger.Msg($"  ToString:           {objectToString}");
        MelonLogger.Msg($"  Prop.type:          {propTypeValue} ({typeName})");
        MelonLogger.Msg($"  Is Weapon:          {isWeapon}");
        MelonLogger.Msg($"  Is WeaponMelee:     {isMelee}");
        MelonLogger.Msg($"  CLR wrapper check:  {clrIsMelee}");
        MelonLogger.Msg($"  WeaponMelee proxy:  {(meleeProxy != null ? "CREATED" : "<none>")}");
        MelonLogger.Msg($"  Is Shield:          {isShield}");
        MelonLogger.Msg("------------------------------------------------");
    }

    private static bool IsMeleePropType(int value)
    {
        return value == 2  || // Axe
               value == 3  || // Sword
               value == 4  || // Dagger
               value == 5  || // Spear
               value == 10 || // Blunt
               value == 21 || // Longsword
               value == 22 || // LongAxe
               value == 23 || // Hammer
               value == 400;  // SmallAxe
    }

    private static string GetPropTypeName(int value)
    {
        return value switch
        {
            -1 => "Undefined",
            1 => "Shield",
            2 => "Axe",
            3 => "Sword",
            4 => "Dagger",
            5 => "Spear",
            6 => "Bow",
            7 => "Arrow",
            8 => "Key",
            9 => "KeyHole",
            10 => "Blunt",
            11 => "Crystal",
            12 => "HealthPotion",
            13 => "PotionLid",
            14 => "Staff",
            15 => "DroneBattery",
            16 => "SoulStaff",
            17 => "KineticStaff",
            18 => "HealthPotionSmall",
            19 => "MiniMapDevice",
            20 => "Crossbow",
            21 => "Longsword",
            22 => "LongAxe",
            23 => "Hammer",
            30 => "GemOrange",
            31 => "GemBlue",
            32 => "GemRed",
            33 => "GemWhite",
            40 => "Bomb",
            41 => "IceGrenade",
            42 => "QuadDamagePotion",
            43 => "HastePotion",
            44 => "InvisibilityPotion",
            45 => "ResurrectPotion",
            46 => "XmasOrnament",
            47 => "DeathWhistle",
            48 => "PumpkinBomb",
            50 => "Loot",
            51 => "Bone",
            60 => "TrophyChest",
            61 => "TrophyNovaGuild",
            62 => "TrophySkullCrown",
            63 => "TrophyZombie",
            75 => "Horn",
            76 => "HockeyStick",
            100 => "Door",
            101 => "Lock",
            102 => "Lever",
            103 => "Receptacle",
            104 => "Valve",
            105 => "BatteryReceptacle",
            106 => "SoulStaffReceptacle",
            107 => "CrystalReceptacle",
            108 => "PotionReceptacle",
            109 => "TrophyReceptacle",
            110 => "Ball",
            111 => "ChessPiece",
            112 => "GemReceptacle",
            120 => "Torch",
            121 => "SkeletonKey",
            122 => "Dice",
            199 => "LIVTablet",
            200 => "Item",
            205 => "Pushable",
            300 => "C1911",
            301 => "C1911Magazine",
            302 => "M16",
            303 => "M16Magazine",
            400 => "SmallAxe",
            _ => "Unknown"
        };
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int PROP_ROOT_HAND_VELOCITY_OFFSET = 0x128;
    private const int PROP_ROOT_HAND_ANGULAR_VELOCITY_OFFSET = 0x148;

    private static void CaptureMouseSwing(IntPtr handsPtr, bool isLeft)
    {
        bool attackHeld = IsDown(isLeft ? VK_LBUTTON : VK_RBUTTON);

        if (isLeft)
        {
            // Start exactly once on the button's rising edge.
            if (attackHeld && !_leftAttackHeld)
            {
                IntPtr rootPtr = GetPropRootFromHands(handsPtr, true);
                IntPtr propPtr = rootPtr == IntPtr.Zero
                    ? IntPtr.Zero
                    : ReadObjectPointer(rootPtr, PROP_ROOT_PROP_OFFSET);

                if (IsHeldMelee(propPtr))
                {
                    _leftSwingActive = true;
                    _leftSwingStartTime = Time.time;
                    _leftSwingRotationInitialized = false;
                    MelonLogger.Msg("[SWING] LEFT: scripted vertical cut started.");
                }
            }

            _leftAttackHeld = attackHeld;

            // Releasing the button does not restart or extend the current cut.
            return;
        }

        if (attackHeld && !_rightAttackHeld)
        {
            IntPtr rootPtr = GetPropRootFromHands(handsPtr, false);
            IntPtr propPtr = rootPtr == IntPtr.Zero
                ? IntPtr.Zero
                : ReadObjectPointer(rootPtr, PROP_ROOT_PROP_OFFSET);

            if (IsHeldMelee(propPtr))
            {
                _rightSwingActive = true;
                _rightSwingStartTime = Time.time;
                _rightSwingRotationInitialized = false;
                MelonLogger.Msg("[SWING] RIGHT: scripted horizontal cut started.");
            }
        }

        _rightAttackHeld = attackHeld;
    }

    private static IntPtr GetPropRootFromHands(IntPtr handsPtr, bool isLeft)
    {
        if (handsPtr == IntPtr.Zero)
            return IntPtr.Zero;

        return ReadObjectPointer(
            handsPtr,
            isLeft ? PROP_ROOT_LEFT_OFFSET : PROP_ROOT_RIGHT_OFFSET);
    }

    private static bool IsHeldMelee(IntPtr propPtr)
    {
        if (propPtr == IntPtr.Zero)
            return false;

        try
        {
            int type = Marshal.ReadInt32(IntPtr.Add(propPtr, PROP_TYPE_OFFSET));
            return IsMeleePropType(type);
        }
        catch
        {
            return false;
        }
    }

    private static void DynamicAnchorPhysicsPostfix(object __instance)
    {
        if (__instance == null)
            return;

        try
        {
            IntPtr anchorPtr = GetNativePointer(__instance);
            if (anchorPtr == IntPtr.Zero)
                return;

            IntPtr leftAnchor = GetHeldDynamicAnchorFromHands(true);
            IntPtr rightAnchor = GetHeldDynamicAnchorFromHands(false);

            if (_leftSwingActive && anchorPtr == leftAnchor)
                ApplySyntheticRigidbodySwing(anchorPtr, true);

            if (_rightSwingActive && anchorPtr == rightAnchor)
                ApplySyntheticRigidbodySwing(anchorPtr, false);
        }
        catch (Exception ex)
        {
            if (!_loggedSwingWriteError)
            {
                _loggedSwingWriteError = true;
                MelonLogger.Warning($"[SWING] DynamicAnchor physics error: {ex.Message}");
            }
        }
    }

    private static IntPtr GetHeldDynamicAnchorFromHands(bool isLeft)
    {
        // We already know the held Prop from PropRoot.prop. Prop.dynamicAnchor
        // is a native object pointer at Prop + 0x1A8.
        IntPtr hands = FindAnyHandsPointer();
        if (hands == IntPtr.Zero)
            return IntPtr.Zero;

        int rootOffset = isLeft ? PROP_ROOT_LEFT_OFFSET : PROP_ROOT_RIGHT_OFFSET;
        IntPtr root = ReadObjectPointer(hands, rootOffset);
        if (root == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr prop = ReadObjectPointer(root, PROP_ROOT_PROP_OFFSET);
        if (prop == IntPtr.Zero)
            return IntPtr.Zero;

        int type;
        try { type = Marshal.ReadInt32(IntPtr.Add(prop, PROP_TYPE_OFFSET)); }
        catch { return IntPtr.Zero; }

        if (!IsMeleePropType(type))
            return IntPtr.Zero;

        return ReadObjectPointer(prop, PROP_DYNAMIC_ANCHOR_OFFSET);
    }

    private static IntPtr _lastHandsPointer;

    private static IntPtr FindAnyHandsPointer()
    {
        // VRControllerHands.Update stores the current instance here. This avoids
        // a scene-wide FindObjectOfType call inside the physics loop.
        return _lastHandsPointer;
    }

    private static void ApplySyntheticRigidbodySwing(IntPtr anchorPtr, bool isLeft)
    {
        IntPtr rbPtr = ReadObjectPointer(anchorPtr, DYNAMIC_ANCHOR_RIGIDBODY_OFFSET);
        if (rbPtr == IntPtr.Zero)
            return;

        float elapsed = Time.time - (isLeft ? _leftSwingStartTime : _rightSwingStartTime);

        if (elapsed >= SWING_TOTAL_TIME)
        {
            if (isLeft)
            {
                _leftSwingActive = false;
                _leftSwingRotationInitialized = false;
            }
            else
            {
                _rightSwingActive = false;
                _rightSwingRotationInitialized = false;
            }
            return;
        }

        Type? rbType = HarmonyLib.AccessTools.TypeByName("UnityEngine.Rigidbody");
        object? rb = WrapIl2CppObject(rbType, rbPtr);
        if (rb == null)
            return;

        try
        {
            PropertyInfo? rotationProp =
                HarmonyLib.AccessTools.Property(rb.GetType(), "rotation");
            PropertyInfo? angularVelocityProp =
                HarmonyLib.AccessTools.Property(rb.GetType(), "angularVelocity");
            PropertyInfo? velocityProp =
                HarmonyLib.AccessTools.Property(rb.GetType(), "velocity");

            if (rotationProp == null)
                return;

            Quaternion currentRotation =
                (Quaternion)(rotationProp.GetValue(rb) ?? Quaternion.identity);

            // Capture the weapon's actual orientation at the instant the attack
            // begins. All subsequent rotations are relative to this orientation,
            // so the cut works regardless of how the player is holding the sword.
            Quaternion startRotation;
            bool initialized = isLeft
                ? _leftSwingRotationInitialized
                : _rightSwingRotationInitialized;

            if (!initialized)
            {
                startRotation = currentRotation;

                if (isLeft)
                {
                    _leftSwingStartRotation = startRotation;
                    _leftSwingRotationInitialized = true;
                }
                else
                {
                    _rightSwingStartRotation = startRotation;
                    _rightSwingRotationInitialized = true;
                }
            }
            else
            {
                startRotation = isLeft
                    ? _leftSwingStartRotation
                    : _rightSwingStartRotation;
            }

            // No wind-up: start from the player's current weapon rotation
            // and immediately sweep through the full vertical cut.
            float angle;
            float angularSpeed;
            float strike;

            float t = Mathf.Clamp01(elapsed / SWING_STRIKE_TIME);
            float smooth = t * t * (3f - 2f * t);

            // Start at the captured rotation and rotate downward through the
            // full strike angle. This avoids the old "already wound up"
            // starting pose.
            angle = Mathf.Lerp(0f, SWING_STRIKE_ANGLE, smooth);

            // Approximate angular velocity for the smoothstep curve.
            float smoothDerivative = 6f * t * (1f - t);
            angularSpeed =
                (SWING_STRIKE_ANGLE / SWING_STRIKE_TIME) * smoothDerivative;

            strike = smooth;

            // Rotate around the weapon's own local axis. This is the important
            // difference from 0.4.8: the attack is an actual rotational sweep,
            // not a straight-line velocity command.
            // Both mouse buttons now use the same vertical cutting plane.
            // The axis is the weapon's local right axis, so the cut remains
            // relative to the way the player is holding the weapon.
            Vector3 localAxis = Vector3.right;

            float signedAngle = angle;
            Quaternion targetRotation =
                startRotation * Quaternion.AngleAxis(signedAngle, localAxis);

            rotationProp.SetValue(rb, targetRotation, null);

            // Keep some angular velocity so Unity's physics/collision systems
            // see the weapon as actively rotating, rather than teleporting its
            // orientation between poses.
            Vector3 worldAxis =
                startRotation * localAxis;
            float signedAngularSpeed =
                isLeft ? angularSpeed : -angularSpeed;
            Vector3 angularVelocity =
                worldAxis * (signedAngularSpeed * Mathf.Deg2Rad);

            angularVelocityProp?.SetValue(rb, angularVelocity, null);

            // Add a small forward movement while striking. This gives the cut
            // some body/weapon follow-through without turning it back into
            // a mouse-driven linear swing.
            Vector3 forwardDirection = startRotation * Vector3.forward;
            float forwardSpeed = elapsed < SWING_WINDUP_TIME
                ? 0f
                : SWING_FORWARD_SPEED * strike;
            Vector3 attackLinearVelocity = forwardDirection * forwardSpeed;

            velocityProp?.SetValue(rb, attackLinearVelocity, null);

            IntPtr rootPtr = GetPropRootForSide(isLeft);
            if (rootPtr != IntPtr.Zero)
            {
                WriteVector3(
                    rootPtr,
                    PROP_ROOT_HAND_VELOCITY_OFFSET,
                    attackLinearVelocity);

                WriteVector3(
                    rootPtr,
                    PROP_ROOT_HAND_ANGULAR_VELOCITY_OFFSET,
                    angularVelocity);
            }
        }
        catch (Exception ex)
        {
            if (!_loggedSwingWriteError)
            {
                _loggedSwingWriteError = true;
                MelonLogger.Warning(
                    $"[SWING] Rigidbody rotational-cut write failed: {ex.Message}");
            }
        }
    }

    private static IntPtr GetPropRootForSide(bool isLeft)
    {
        IntPtr hands = FindAnyHandsPointer();
        if (hands == IntPtr.Zero)
            return IntPtr.Zero;

        return GetPropRootFromHands(hands, isLeft);
    }

    private static bool _loggedSwingStart;
    private static bool _loggedSwingWriteError;

    private static IntPtr GetNativePointer(object instance)
    {
        if (instance == null)
            return IntPtr.Zero;

        try
        {
            Type? current = instance.GetType();
            while (current != null)
            {
                PropertyInfo? pointerProperty = current.GetProperty(
                    "Pointer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (pointerProperty?.PropertyType == typeof(IntPtr))
                    return (IntPtr)(pointerProperty.GetValue(instance) ?? IntPtr.Zero);

                FieldInfo? pointerField = current.GetField(
                    "Pointer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (pointerField?.FieldType == typeof(IntPtr))
                    return (IntPtr)(pointerField.GetValue(instance) ?? IntPtr.Zero);

                current = current.BaseType;
            }
        }
        catch
        {
            // Return zero; callers treat it as unavailable.
        }

        return IntPtr.Zero;
    }

    private static IntPtr ReadObjectPointer(IntPtr objectPtr, int offset)
    {
        if (objectPtr == IntPtr.Zero)
            return IntPtr.Zero;

        return Marshal.ReadIntPtr(IntPtr.Add(objectPtr, offset));
    }

    private static object? WrapIl2CppObject(Type? type, IntPtr pointer)
    {
        if (type == null || pointer == IntPtr.Zero)
            return null;

        ConstructorInfo? ctor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(IntPtr) },
            modifiers: null);

        if (ctor != null)
            return ctor.Invoke(new object[] { pointer });

        foreach (ConstructorInfo candidate in type.GetConstructors(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr))
                return candidate.Invoke(new object[] { pointer });
        }

        return null;
    }

    private static string FormatType(Type? type)
    {
        if (type == null)
            return "<null>";

        return $"{type.FullName ?? type.Name} (assembly={type.Assembly.GetName().Name})";
    }

    private static void UpdatePrefix(object __instance)
    {
        if (__instance == null || _addExternalMoveStickInput == null)
            return;

        Vector2 move = GetKeyboardMove();
        _addExternalMoveStickInput.Invoke(__instance, new object[] { move });

        if (!_loggedInput && (move.x != 0 || move.y != 0))
        {
            _loggedInput = true;
            MelonLogger.Msg($"WASD active: {move.x:0.00}, {move.y:0.00}");
        }
    }

    private static void AButtonPrefix(ref bool __result)
    {
        __result = IsDown(VK_MBUTTON);

        if (!_loggedA && __result)
        {
            _loggedA = true;
            MelonLogger.Msg("Middle Mouse -> Quest A active.");
        }
    }

    private static void AButtonDownPrefix(ref bool __result)
    {
        __result = IsPressed(VK_MBUTTON);
    }

    private static void AButtonUpPrefix(ref bool __result)
    {
        __result = IsReleased(VK_MBUTTON);
    }

    private static void GetButtonPrefix(object button, ref bool __result)
    {
        if (button is Enum e && Convert.ToInt32(e) == 0)
            __result = IsDown(VK_MBUTTON);
    }

    private static void GetButtonDownPrefix(object button, ref bool __result)
    {
        if (button is Enum e && Convert.ToInt32(e) == 0)
            __result = IsPressed(VK_MBUTTON);
    }

    private static int _middleMouseEdgeFrame = -1;
    private static bool _middleMouseEdgePressed;
    private static bool _middleMouseEdgeReleased;
    private static bool _middleMouseEdgeHeld;

    private static void UpdateMiddleMouseEdges()
    {
        int frame = Time.frameCount;
        if (_middleMouseEdgeFrame == frame)
            return;

        bool current = IsDown(VK_MBUTTON);
        _middleMouseEdgePressed = current && !_middleMouseEdgeHeld;
        _middleMouseEdgeReleased = !current && _middleMouseEdgeHeld;
        _middleMouseEdgeHeld = current;
        _middleMouseEdgeFrame = frame;
    }

    private static bool IsPressed(int key)
    {
        if (key != VK_MBUTTON)
            return false;

        UpdateMiddleMouseEdges();
        return _middleMouseEdgePressed;
    }

    private static bool IsReleased(int key)
    {
        if (key != VK_MBUTTON)
            return false;

        UpdateMiddleMouseEdges();
        return _middleMouseEdgeReleased;
    }

    private static Vector2 GetKeyboardMove()
    {
        float x = 0f;
        float y = 0f;

        if (IsDown(VK_W)) y += 1f;
        if (IsDown(VK_S)) y -= 1f;
        if (IsDown(VK_A)) x -= 1f;
        if (IsDown(VK_D)) x += 1f;

        float lenSq = x * x + y * y;
        if (lenSq > 1f)
        {
            float inv = 1.0f / MathF.Sqrt(lenSq);
            x *= inv;
            y *= inv;
        }

        return new Vector2(x, y);
    }


    private static unsafe void WriteVector3(IntPtr basePtr, int offset, Vector3 value)
    {
        if (basePtr == IntPtr.Zero) return;
        byte* p = (byte*)basePtr.ToPointer() + offset;
        *(float*)(p + 0) = value.x;
        *(float*)(p + 4) = value.y;
        *(float*)(p + 8) = value.z;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
