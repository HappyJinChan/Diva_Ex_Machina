# City_Export — 城市模組匯出包（Unity URP）

## 內容

| 路徑 | 內容 |
| --- | --- |
| `Models/BLD_*.fbx` | 31 個模組（素材庫 30 個 ＋ Example 2 的雙簷線頂蓋 `BLD_POD_CapEdge2`）。原點在格子中心底面，旋轉已寫進網格，1 單位 = 1 m。 |
| `Textures/T_*.png` | 7 張貼圖。牆面每 25 m × 24.5 m 重複一次（一張貼圖內含 5 個 4.9 m 的樓層），屋頂每 25 m × 25 m 重複一次。 |
| `Editor/CityImportSetup.cs` | Unity 編輯器腳本：自動匯入設定、建立 URP 材質、對應材質槽。 |
| `manifest.json` | 每個模組的高度（層）、輪廓（W/N）、類別、門洞開口方向、標準朝向說明、材質槽、範圍、三角面數。 |
| `layout_District_1km.json` | 目前 1 km² 街區的佈局（318 件）：模組、所屬群組、格子、層數、旋轉、座標。 |

## 放進 Unity

1. 把整個 `City_Export` 資料夾拖進 `Assets/`。FBX 會自動套用匯入設定（Bake Axis Conversion、不產生碰撞體等）。
2. 選單 **Tools > City > 1. Create URP Materials**：在 `City_Export/Materials/` 建立 7 個 URP/Lit 材質並指定貼圖。
3. 選單 **Tools > City > 2. Remap FBX Materials**：把每個 FBX 的材質槽依名稱對應到這些材質。
4. 把 `Models/` 裡的 FBX 拖進場景就能用。

如果在第 1 步之前就已經匯入過 FBX，匯入設定不會自動重套：對 `Models` 資料夾按右鍵 > Reimport 即可。

## 方向驗證（第一次請做）

Blender 是 Z 向上的右手座標，Unity 是 Y 向上的左手座標。依目前的 FBX 匯出設定推算：

- Blender (x, y, z) → Unity (−x, z, −y)
- Blender 繞 Z 軸逆時針 θ → Unity 繞 Y 軸 −θ

`layout_District_1km.json` 裡的 `pos_unity_expected` 和 `rot_y_unity_expected` 就是用這個推算的。**這個推算還沒在 Unity 實測過**，請先驗證一次：

1. 把 `BLD_FLR_N_Silo_Start` 放在原點、旋轉 0。燃料箱在 Blender 裡朝 +X。
2. 推算的結果是燃料箱朝 Unity 的 **−X**。如果實際朝向不同，告訴我，我改匯出設定或佈局換算。

## 重新匯出

模組有修改時，在 Blender 打開 `Building_v2_textured.blend` > Scripting 分頁 > `ExportModules.py` > Run Script。整個 `City_Export` 會重新輸出到 .blend 旁邊，不會改動 .blend 本身。之後在 Unity 對 `Models` 資料夾 Reimport 即可（材質對應會保留）。

## 已知事項

- **開放網格**：`BLD_FLR_N_Silo_Mid`、`BLD_BRG_Span`、`BLD_BASE_W_GateL`、`BLD_POD_Gate_L`、`BLD_POD_Gate_T` 有破洞（Blender 端的原始模型）。顯示沒問題，但做 RayFire 預碎裂前要先補好。
- **凹凸效果**：Blender 材質裡的凹凸節點不會帶進 Unity；需要的話之後再補法線貼圖。
- **`BLD_FLR_W_FlatSide`**（原 Floor5）用途待確認，生成器沒有使用。
- **`BLD_POD_CapEdge2`** 只存在於 Example 2，生成器目前用的是單簷線的 `BLD_POD_CapEdge`。
- 組合規則與命名對照：見專案文件 `城市模組_組合規則_v2.md`。
