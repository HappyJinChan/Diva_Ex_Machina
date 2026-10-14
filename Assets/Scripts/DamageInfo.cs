using UnityEngine;

/// <summary>攻擊類型。受擊方可以依類型播不同的回饋（例如玩家被近戰打中時用比較重的鏡頭震動）。</summary>
public enum DamageKind
{
    Normal = 0,   // 子彈、爆炸、撞擊等（預設值）
    Melee = 1,    // 近戰
}

public struct DamageInfo
{
    public float physical;
    public float explosion;
    public float energy;
    public float cold;

    // 衝擊值：扣目標的 ACS，扣到 0 觸發失衡（見 EntityStatusEffects）。
    // 不是傷害：不吃防禦、不吃暴擊；子彈的衝擊值跟傷害一樣受距離衰減。
    public float impact;

    // 攻擊類型。預設 Normal；近戰的發出端（敵人的 MeleeManager、玩家的 MeleeHitbox）設成 Melee。
    public DamageKind kind;

    public DamageInfo(float physical, float explosion, float energy, float cold)
    {
        this.physical = physical;
        this.explosion = explosion;
        this.energy = energy;
        this.cold = cold;
        this.impact = 0f;
        this.kind = DamageKind.Normal;
    }

    public DamageInfo(float physical, float explosion, float energy, float cold, float impact)
    {
        this.physical = physical;
        this.explosion = explosion;
        this.energy = energy;
        this.cold = cold;
        this.impact = impact;
        this.kind = DamageKind.Normal;
    }
}

public interface IDamageable
{
    void TakeDamage(DamageInfo damage, GameObject attacker);
}