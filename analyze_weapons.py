"""
射擊場數據分析：找出「太強」的武器。

用法（在專案根目錄）：
    python analyze_weapons.py                       # 讀 ShootingRangeData/ 底下所有 session
    python analyze_weapons.py path/to/folder        # 指定資料夾

讀取 ShootingRangeScoreboard 匯出的 *_kills.csv 與 *_loadouts.csv。

判斷方式：
  · 指標用 interval_since_prev_kill（距上一次擊殺的間隔）。它不受流彈影響，
    比 time_to_kill 可靠；缺值時退回 time_to_kill。
  · 依（距離, 靶子血量）分組，每組內每把武器取「中位數」—— 對單次手滑不敏感。
  · relative = 這把武器的中位數 ÷ 該組最快的中位數。1.0 = 該組最快。
  · 一把武器在大部分組別都是最快、而且領先幅度大，就標記為「可能太強」。
    門檻在下方常數調整。
"""

import sys
from glob import glob
from pathlib import Path

import pandas as pd

# ───── 判斷門檻 ─────
MIN_SAMPLES = 3          # 一組裡少於這麼多筆的武器不參與判斷（數據不夠）
WIN_SHARE_FLAG = 0.75    # 在 ≥ 75% 的組別最快
LEAD_FLAG = 1.30         # 而且第二名平均比它慢 ≥ 30%
SLOW_FLAG = 2.00         # 在所有組別都比最快的慢 2 倍以上 → 可能太弱


def load(folder: Path):
    kill_files = sorted(glob(str(folder / "*_kills.csv")))
    if not kill_files:
        sys.exit(f"在 {folder} 找不到 *_kills.csv")

    kills = pd.concat((pd.read_csv(f) for f in kill_files), ignore_index=True)
    load_files = sorted(glob(str(folder / "*_loadouts.csv")))
    loadouts = pd.concat((pd.read_csv(f) for f in load_files), ignore_index=True) if load_files else None

    print(f"讀入 {len(kill_files)} 個 session、{len(kills)} 筆擊殺")
    return kills, loadouts


def short_name(combo: str) -> str:
    """'L:SMG|R:SMG|LS:-|RS:-' → 'SMG x2'；左右不同則 'SMG + Shotgun'；肩武器加在後面。"""
    slots = dict(p.split(":", 1) for p in combo.split("|") if ":" in p)
    if not slots:
        return combo
    l, r = slots.get("L", "-"), slots.get("R", "-")
    hands = [w for w in (l, r) if w != "-"]
    if len(hands) == 2 and l == r:
        name = f"{l} x2"
    else:
        name = " + ".join(hands) or "(空手)"
    shoulders = [w for w in (slots.get("LS", "-"), slots.get("RS", "-")) if w != "-"]
    if shoulders:
        name += " / 肩:" + "+".join(shoulders)
    return name


def prepare(kills: pd.DataFrame) -> pd.DataFrame:
    df = kills.copy()

    # 指標：優先用間隔，沒有（還沒結算的擊殺）就用 time_to_kill
    df["time"] = df["interval_since_prev_kill"].fillna(df["time_to_kill"])

    # 距離分組：四捨五入到整數公尺。沒有距離（舊資料）就從名稱裡抓 "10m"
    if df["distance"].isna().any():
        from_name = df["enemy_name"].str.extract(r"(\d+)\s*m", expand=False).astype(float)
        df["distance"] = df["distance"].fillna(from_name)
    df["distance"] = df["distance"].round()

    df["weapon"] = df["weapon_combo"].fillna("(unknown)").map(short_name)

    # 有效 DPS：靶子血量 ÷ 花的時間（含換目標時間，所以比面板 DPS 低）
    df["effective_dps"] = df["target_hp"] / df["time"].where(df["time"] > 0)
    return df


def summarize(df: pd.DataFrame) -> pd.DataFrame:
    g = df.groupby(["distance", "target_hp", "weapon"])["time"]
    table = g.agg(n="count", median="median", mean="mean", std="std").reset_index()

    # 每組（距離 × 血量）裡的相對速度，只用樣本數夠的武器
    ok = table[table["n"] >= MIN_SAMPLES].copy()
    best = ok.groupby(["distance", "target_hp"])["median"].transform("min")
    ok["relative"] = ok["median"] / best
    ok["rank"] = ok.groupby(["distance", "target_hp"])["median"].rank(method="min")

    return table.merge(ok[["distance", "target_hp", "weapon", "relative", "rank"]],
                       on=["distance", "target_hp", "weapon"], how="left")


def verdict(table: pd.DataFrame) -> pd.DataFrame:
    ok = table.dropna(subset=["relative"])
    if ok.empty:
        return pd.DataFrame()

    rows = []
    for weapon, w in ok.groupby("weapon"):
        buckets = len(w)
        wins = int((w["rank"] == 1).sum())

        # 領先幅度：在它拿第一的組別裡，第二名比它慢多少
        leads = []
        for _, r in w[w["rank"] == 1].iterrows():
            same = ok[(ok["distance"] == r["distance"]) & (ok["target_hp"] == r["target_hp"])]
            others = same[same["weapon"] != weapon]["relative"]
            if not others.empty:
                leads.append(others.min())
        avg_lead = sum(leads) / len(leads) if leads else float("nan")

        win_share = wins / buckets
        if buckets >= 2 and win_share >= WIN_SHARE_FLAG and avg_lead >= LEAD_FLAG:
            flag = "可能太強"
        elif w["relative"].min() >= SLOW_FLAG:
            flag = "可能太弱"
        else:
            flag = ""

        rows.append({
            "weapon": weapon,
            "buckets": buckets,
            "wins": wins,
            "win_share": round(win_share, 2),
            "avg_lead_when_winning": round(avg_lead, 2),
            "worst_relative": round(w["relative"].max(), 2),
            "flag": flag,
        })

    return pd.DataFrame(rows).sort_values("win_share", ascending=False)


def main():
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("ShootingRangeData")
    kills, loadouts = load(folder)
    df = prepare(kills)
    table = summarize(df)

    pd.set_option("display.width", 200)
    pd.set_option("display.max_columns", 30)

    print("\n=== 每組（距離 × 血量）各武器的時間中位數（秒）===")
    pivot = table.pivot_table(index=["target_hp", "distance"], columns="weapon", values="median")
    print(pivot.round(2))

    print("\n=== 相對速度（1.0 = 該組最快；樣本 < %d 的不列入）===" % MIN_SAMPLES)
    rel = table.pivot_table(index=["target_hp", "distance"], columns="weapon", values="relative")
    print(rel.round(2))

    print("\n=== 樣本數 ===")
    print(table.pivot_table(index=["target_hp", "distance"], columns="weapon", values="n", aggfunc="sum").fillna(0).astype(int))

    print("\n=== 判斷 ===")
    v = verdict(table)
    print(v.to_string(index=False) if not v.empty else f"樣本不夠：每組每把武器至少需要 {MIN_SAMPLES} 筆。")

    out = folder / "summary.csv"
    table.to_csv(out, index=False)
    print(f"\n完整表格已存到 {out}")


if __name__ == "__main__":
    main()
