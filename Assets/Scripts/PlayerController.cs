using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public Vector2 LastMoveInput { get; private set; }
    public static PlayerController Instance { get; private set; }
    private PlayerControllers playerControls;

    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private AttackManager attackManager;
    private bool _weaponsSuppressed;   // 迫擊砲鎖定模式中 / 剛退出、按鍵還沒放開
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        playerControls = new PlayerControllers();
    }

    private void OnEnable()
    {
        playerControls.Player.Enable();
    }

    private void OnDisable()
    {
        playerControls.Disable();
    }

    // Gameplay input gate (UI / melee dash lock, etc.)
    private bool IsGameplayInputBlocked()
    {
        // 1) Lock gameplay input when in Equipment / Crafting UI
        if (UIManager.Instance != null && UIManager.Instance.currentCameraSet != 0)
            return true;

        // 2) 換裝硬直（支援艙命中後的短暫停頓）
        if (playerMovement != null && playerMovement.IsMovementFrozen)
            return true;

        return false;
    }

    void Update()
    {
        // Block all gameplay input (UI lock / melee dash lock)
        if (IsGameplayInputBlocked())
        {
            LastMoveInput = Vector2.zero; 
            // Clear movement intent so RotateCharacter / animations don't keep the last input direction.
            if (playerMovement != null)
            {
                playerMovement.HorizontalMovement(0f, 0f);
                playerMovement.EndAssaultBoost();   // 打開裝備 / 合成介面時不要讓角色繼續衝出去
            }

            return; // Skip Jump / Dash / Attack / Reload / AutoAim etc.
        }

        Vector2 movementInput = playerControls.Player.Move.ReadValue<Vector2>();
        LastMoveInput = movementInput;

        playerMovement.HorizontalMovement(movementInput.x, movementInput.y);

        if (playerControls.Player.Jump.IsPressed())
        {
            playerMovement.JumpAction();
        }

        if (playerControls.Player.Sprint.WasPressedThisFrame())
        {
            playerMovement.DashAction();
        }

        // 突擊推進：按一下開啟、再按一下關閉
        if (playerControls.Player.AssaultBoost.WasPressedThisFrame())
        {
            playerMovement.AssaultBoostAction();
        }

        // Auto Aim toggle (Middle Mouse)
        if (playerControls.Player.AutoAim.WasPressedThisFrame())
        {
            if (PlayerAiming.Instance != null)
                PlayerAiming.Instance.ToggleAutoAim();
        }

        // 迫擊砲鎖定模式：滑鼠左右鍵交給 SupplyManager，左右手、肩膀武器、近戰、換彈都不動作。
        // 退出模式後，左右鍵要先放開一次才恢復（避免放開炮擊 / 按 G 取消時，按著的鍵直接開火）。
        var supply = SupplyManager.Instance;
        if (supply != null && supply.IsMortarTargeting)
        {
            if (!_weaponsSuppressed)
            {
                playerMovement.CancelAttackInput();
                _weaponsSuppressed = true;
            }
            return;
        }
        if (_weaponsSuppressed)
        {
            if (playerControls.Player.LeftHandAttack.IsPressed() || playerControls.Player.RightHandAttack.IsPressed())
                return;
            _weaponsSuppressed = false;
        }

        // Reload gate:
        bool reloadHeld = playerControls.Player.Reload.IsPressed();
        bool leftPressed = playerControls.Player.LeftHandAttack.WasPressedThisFrame();
        bool rightPressed = playerControls.Player.RightHandAttack.WasPressedThisFrame();
        bool leftShoulderPressed = playerControls.Player.LeftShoulderAttack.WasPressedThisFrame();
        bool rightShoulderPressed = playerControls.Player.RightShoulderAttack.WasPressedThisFrame();

        var stats = PlayerStats.Instance;

        if (reloadHeld && (leftPressed || rightPressed || leftShoulderPressed || rightShoulderPressed))
        {
            if (leftPressed)
            {
                bool leftIsRange = (stats != null && stats.leftHand.weaponKind == HandWeaponKind.Range);
                if (leftIsRange) attackManager.StartReload(attackManager.leftHandWeapon);
                else playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.leftHandWeapon, playerControls.Player.LeftHandAttack);
            }

            if (rightPressed)
            {
                bool rightIsRange = (stats != null && stats.rightHand.weaponKind == HandWeaponKind.Range);
                if (rightIsRange) attackManager.StartReload(attackManager.rightHandWeapon);
                else playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.rightHandWeapon, playerControls.Player.RightHandAttack);
            }

            // 肩武器只有遠程：R + 肩武器鍵 = 提前換彈。沒裝肩武器（bullet 是 null）就不理會。
            if (leftShoulderPressed && HasShoulderWeapon(attackManager.leftShoulderWeapon))
                attackManager.StartReload(attackManager.leftShoulderWeapon);

            if (rightShoulderPressed && HasShoulderWeapon(attackManager.rightShoulderWeapon))
                attackManager.StartReload(attackManager.rightShoulderWeapon);

            return;
        }

        // Normal attack
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.leftHandWeapon, playerControls.Player.LeftHandAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.rightHandWeapon, playerControls.Player.RightHandAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.leftShoulderWeapon, playerControls.Player.LeftShoulderAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.rightShoulderWeapon, playerControls.Player.RightShoulderAttack);
    }

    /// <summary>這個肩槽有裝遠程武器（AttackManager 清空武器時會把 bullet 設成 null）。</summary>
    private static bool HasShoulderWeapon(Weapon w) => w != null && w.bullet != null;

}