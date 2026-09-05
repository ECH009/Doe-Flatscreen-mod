# Dungeons of Eternity Keyboard/Mouse Mod 0.4.4

This build keeps the 0.4.3 held-Prop detection and fixes the melee swing path.

## Controls
- W/A/S/D: movement
- Middle Mouse: Quest A
- Hold Left Mouse + move mouse: left-hand melee swing
- Hold Right Mouse + move mouse: right-hand melee swing

## 0.4.4 melee change
The previous build only wrote `PropRoot.handVelocity` / `handAngularVelocity`. Those values are recomputed by the game's `PropRoot.UpdatePhysics()`, so the write was not sufficient to produce physical weapon motion.

This build:
1. Captures mouse movement during `VRControllerHands.Update()`.
2. Resolves the held melee Prop from the verified native PropRoot offsets.
3. Resolves `Prop.dynamicAnchor` at `Prop + 0x1A8`.
4. Resolves `DynamicAnchorBase.r` at `DynamicAnchor + 0x38`.
5. Patches `DynamicAnchorBase.UpdatePhysics()` with a postfix.
6. Writes the synthetic velocity and angular velocity to the actual held Rigidbody after the game's normal physics update.
7. Also writes the corresponding PropRoot hand-motion fields so `WeaponMelee.UpdatePhysics()` can see the synthetic hand speed.

This deliberately does not depend on `Camera.main`.

## Expected log
When a melee weapon is held and a mouse button is pressed/moved, the MelonLoader log should contain a line similar to:

`[SWING] LEFT: melee mouse input captured; Prop.type=3 (Sword)`

If the log appears but the weapon still does not physically move, the next step is to instrument `DynamicAnchorBase.UpdatePhysics()` and verify the Rigidbody's velocity immediately before and after the postfix.


0.4.7 changes: both LMB and RMB use a deeper vertical rotational cut, with a small forward attack movement during the strike. Mouse movement is ignored.
