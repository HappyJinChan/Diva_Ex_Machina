using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public Vector2 LastMoveInput { get; private set; }
    public static PlayerController Instance { get; private set; }
    private PlayerControllers playerControls;

    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private AttackManager attackManager;
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

        // Reload gate:
        bool reloadHeld = playerControls.Player.Reload.IsPressed();
        bool leftPressed = playerControls.Player.LeftHandAttack.WasPressedThisFrame();
        bool rightPressed = playerControls.Player.RightHandAttack.WasPressedThisFrame();

        var stats = PlayerStats.Instance;

        if (reloadHeld && (leftPressed || rightPressed))
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

            return;
        }

        // Normal attack
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.leftHandWeapon, playerControls.Player.LeftHandAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.rightHandWeapon, playerControls.Player.RightHandAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.leftShoulderWeapon, playerControls.Player.LeftShoulderAttack);
        playerMovement.ProcessAttackFacingAndAttack(attackManager, attackManager.rightShoulderWeapon, playerControls.Player.RightShoulderAttack);
    }

}