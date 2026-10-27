(() => {
  let state = null;
  const element = id => document.getElementById(id);
  const post = (type, value = {}) => window.chrome?.webview?.postMessage({ version: 1, type, payload: value });

  // 6 大模块标题字典
  const MODULE_TITLES = {
    effectpatch: "⚡ 游戏特效与性能优化 (Effect)",
    colormod: "🎨 词缀多色彩染色 (ColorMod)",
    mapbeam: "🌟 神图冲天光柱 (MapBeam)",
    chests: "📦 宝箱掉落标注与透视 (Chests)",
    rumours: "🔮 异界地图传言标注 (Rumours)",
    fontmod: "🔤 游戏字体与排版 (FontMod)",
    price: "💰 原生价格深度注入 (Price)",
    iconpatch: "🖼️ 图标替换 (IconPatch)",
    healthbar: "⚔️ 头顶血条 (HealthBar)",
    mtxpatch: "👑 PoE 2 商城外观特效替换 (MTX)"
  };

  // 1. 词缀染色规则数据池（由 C# 后端真实扫描结果动态加载与维护）
  let CLIENT_STAT_DICTIONARY = [];

  // 2. 神图冲天光柱真实数据 (MapBeamPatchService.DefaultTargets 对齐)
  const MAP_BEAM_DATA = [
    { name: "The Jade Isles", nameZh: "青玉群岛", color: "Farudin", hex: "#c9a84c", enabled: true, desc: "极品高价值掉落神图" },
    { name: "Sealed Vault", nameZh: "封印宝库", color: "Farudin", hex: "#c9a84c", enabled: true, desc: "通货与命运卡宝库" },
    { name: "Derelict Mansion", nameZh: "遗弃宅邸", color: "Farudin", hex: "#c9a84c", enabled: true, desc: "暗金基底高爆率" },
    { name: "Sacred Reservoir", nameZh: "神圣水库", color: "Farudin", hex: "#c9a84c", enabled: true, desc: "高阶水库神图" },
    { name: "Castaway", nameZh: "颠沛领域", color: "Green", hex: "#4c9a5f", enabled: true, desc: "特殊裂隙与深渊" },
    { name: "Untainted Paradise", nameZh: "纯净乐园", color: "Yellow", hex: "#e0b33c", enabled: true, desc: "升级经验圣地" },
    { name: "Azmerian Ranges", nameZh: "阿兹莫里山区", color: "Vaal", hex: "#e64545", enabled: true, desc: "极度危险高收益" }
  ];

  // 3. 宝箱掉落类别识别数据（由客户端实际 chests.datc64 扫描与规则引擎动态驱动）
  let CHESTS_DATA = [];

  // 6. 异界地图传言标注真实数据 (由 C# 客户端扫描与规则引擎提供真实闭环数据)
  let RUMOURS_DATA = [];

  // ── 词缀染色的三层数据模型 ──
  //
  // ① clientAffixes  词缀数据。由后端扫描客户端得到，含后端算好的默认颜色/样式/启用状态。
  //                  只在本次会话内存在，不持久化——落盘会过期，也会成为污染源。
  // ② userConfig     用户配置。只记录与默认值不同的字段，键为 StatId。
  //                  这是唯一需要持久化的东西，体积远小于全量词缀清单。
  // ③ colorModData   视图数据。由 ① 叠加 ② 计算得出，只用于渲染，不持久化。
  //
  // 切换预设只改变 ②，然后重算 ③；① 始终不动，因此默认规则不可能被用户操作污染。
  let clientAffixes = [];
  let userConfig = new Map();
  let colorModData = [];

  // 预设：每份都是一套完整的用户配置副本，彼此独立，与默认值无关。
  // 默认预设的 config 为 null，表示「不做任何覆盖，完全使用 clientAffixes 的默认值」。
  let customPresets = {}; // { [id]: { id, name, config, createdAt } }
  let currentPresetId = "default";
  const PRESET_STORAGE_KEY = "frei_colormod_custom_presets";

  let lastSnapshot = [];
  let isClientSynced = false;
  let currentGroup = "地图";
  let previewType = "map";
  let colorModCurrentPage = 1;
  const COLOR_MOD_PAGE_SIZE = 100;

  // ── 词缀染色的核心计算 ──

  /// 取某个预设对应的配置。默认预设返回 null，表示「不覆盖，全用默认值」。
  function getConfigForPreset(presetId) {
    if (presetId === "default") return null;
    const p = customPresets[presetId];
    return p?.config || null;
  }

  /// 用 clientAffixes + 指定配置重算视图。切换预设、修改颜色都走这里。
  function rebuildView() {
    const cfg = getConfigForPreset(currentPresetId);
    colorModData = clientAffixes.map(base => {
      const ov = cfg ? cfg[base.statId] : null;
      // 用 ?? 而不是 ||：用户明确把 enabled 设成 false 时必须保留 false
      return {
        id: base.statId,
        text: base.displayName,
        displayName: base.displayName,
        group: base.group || "通用",
        color: ov?.color ?? base.color,
        style: ov?.style ?? base.style,
        enabled: ov?.enabled ?? base.enabled,
        tag: base.isDanger ? "高危词缀" : (base.currentColorTag || (base.group + "词缀")),
        isDanger: !!base.isDanger,
        isCustom: false
      };
    });

    // 自定义规则（用户手动新增的条目，不属于客户端词缀）直接附在末尾
    const cfgRules = cfg?._customRules;
    if (Array.isArray(cfgRules)) {
      colorModData = colorModData.concat(cfgRules.map(r => ({ ...r, isCustom: true })));
    }
  }

  /// 把当前视图与默认值的差异提取为配置对象。只保留真正改过的字段。
  function extractDiff() {
    const cfg = {};
    const baseMap = new Map(clientAffixes.map(a => [a.statId, a]));
    for (const row of colorModData) {
      if (row.isCustom) continue;   // 自定义规则单独处理
      const base = baseMap.get(row.id);
      if (!base) continue;

      const diff = {};
      if (row.color !== base.color) diff.color = row.color;
      if (row.style !== base.style) diff.style = row.style;
      if (row.enabled !== base.enabled) diff.enabled = row.enabled;
      if (Object.keys(diff).length > 0) cfg[row.id] = diff;
    }

    const customRules = colorModData.filter(r => r.isCustom);
    if (customRules.length > 0) {
      cfg._customRules = customRules.map(r => ({
        id: r.id, text: r.text, group: r.group,
        color: r.color, style: r.style, tag: r.tag,
        isDanger: !!r.isDanger, enabled: !!r.enabled
      }));
    }
    return cfg;
  }

  /// 提交当前视图的差异到当前预设。
  function commitView() {
    const cfg = extractDiff();
    if (currentPresetId === "default") {
      // 默认预设不可修改：用户一改就自动转成一份新预设，默认本身始终干净
      const newId = "preset_" + Date.now();
      customPresets[newId] = {
        id: newId,
        name: "自定义副本 " + new Date().toLocaleTimeString(),
        config: cfg,
        createdAt: Date.now()
      };
      currentPresetId = newId;
      persistCustomPresets();
      renderPresetSelect();
      showToast("默认预设不可修改，已自动另存为新的自定义预设");
      return;
    }
    customPresets[currentPresetId] = {
      ...customPresets[currentPresetId],
      config: cfg
    };
    persistCustomPresets();
  }

  // ── 预设持久化管理 ──
  function loadCustomPresets() {
    try {
      const raw = localStorage.getItem(PRESET_STORAGE_KEY);
      if (raw) {
        customPresets = JSON.parse(raw) || {};
      }
    } catch (e) {
      customPresets = {};
    }
    renderPresetSelect();
  }

  function persistCustomPresets() {
    try {
      localStorage.setItem(PRESET_STORAGE_KEY, JSON.stringify(customPresets));
    } catch (e) {
      console.error("保存预设失败", e);
    }
  }

  function renderPresetSelect() {
    const sel = element("presetSelect");
    if (!sel) return;
    sel.innerHTML = `<option value="default">🌟 默认预设 (官方推荐)</option>`;
    const ids = Object.keys(customPresets);
    if (ids.length > 0) {
      const grp = document.createElement("optgroup");
      grp.label = "── 自定义预设 ──";
      ids.forEach(id => {
        const p = customPresets[id];
        const opt = document.createElement("option");
        opt.value = id;
        opt.innerText = `⚙️ ${p.name}`;
        grp.appendChild(opt);
      });
      sel.appendChild(grp);
    }
    sel.value = currentPresetId;
    updatePresetActionButtons();
  }

  function updatePresetActionButtons() {
    const btnDel = element("btnDeletePreset");
    if (btnDel) {
      if (currentPresetId === "default") {
        btnDel.classList.add("hidden");
      } else {
        btnDel.classList.remove("hidden");
      }
    }
  }

  function onPresetChange(newId) {
    currentPresetId = newId;
    // 词缀数据不动，只用新预设的配置重算每条的显示属性
    rebuildView();
    colorModCurrentPage = 1;
    renderTable();
    renderLiveCard();
    updatePresetActionButtons();

    if (newId === "default") {
      showToast("已切换到【🌟 默认预设 (官方推荐)】");
    } else if (customPresets[newId]) {
      showToast(`已加载自定义预设【${customPresets[newId].name}】`);
    }
  }

  /**
   * 另存为预设：把当前视图的差异完整复制成一份独立配置。
   *
   * 预设是完整的副本，不与默认规则对照，也不引用 clientAffixes——
   * 这样即使用户把预设改坏，默认预设仍然完好。
   */
  function saveCustomPreset(name) {
    const trimmed = name?.trim();
    if (!trimmed) return "预设名称不能为空";
    if (trimmed === "默认预设" || trimmed.includes("官方推荐") || trimmed.toLowerCase() === "default") {
      return "默认预设为内置方案，禁止覆盖！请使用其他名称保存为自定义预设。";
    }
    const presetId = "preset_" + Date.now();
    customPresets[presetId] = {
      id: presetId,
      name: trimmed,
      config: extractDiff(),
      createdAt: Date.now()
    };
    persistCustomPresets();
    currentPresetId = presetId;
    renderPresetSelect();
    showToast(`🎉 已成功保存自定义预设【${trimmed}】！`);
    return null;
  }

  function deleteCurrentCustomPreset() {
    if (currentPresetId === "default") return;
    const p = customPresets[currentPresetId];
    if (!p) return;
    if (confirm(`确定要删除自定义预设【${p.name}】吗？删除后将无法恢复。`)) {
      delete customPresets[currentPresetId];
      persistCustomPresets();
      currentPresetId = "default";
      // 回到默认预设：词缀数据不变，用空配置重算即可
      rebuildView();
      renderPresetSelect();
      renderTable();
      renderLiveCard();
      showToast(`已删除自定义预设【${p.name}】，并还原为官方默认预设`);
    }
  }

  // ── 批量全选 / 全不选（框选） ──
  function getCurrentFilteredRules() {
    const query = element("searchInput")?.value?.trim().toLowerCase() || "";
    return colorModData.filter(item => {
      const matchGroup = currentGroup === "全部" || item.group === currentGroup;
      const textToMatch = (item.text || item.displayName || "").toLowerCase();
      const tagToMatch = (item.tag || "").toLowerCase();
      return matchGroup && (!query || textToMatch.includes(query) || tagToMatch.includes(query));
    });
  }

  function toggleSelectAll(checked) {
    const list = getCurrentFilteredRules();
    if (list.length === 0) return;
    list.forEach(item => {
      item.enabled = checked;
    });
    commitView();
    renderTable();
    renderLiveCard();
    showToast(checked ? `已全选当前筛选的 ${list.length} 条词缀` : `已取消勾选当前筛选的 ${list.length} 条词缀`);
  }

  // 模块总开关。只登记真正会被 apply* 读取的模块 ——
  // 读取点：colormod(1123) / mapbeam(1141) / chests(1154) / rumours(1166) / iconpatch(413)。
  // fontmod 与 price 的应用动作无条件执行，没有「关闭即还原」语义，
  // 其卡片开关已移除，不再登记。
  const moduleMasters = {
    colormod: true,
    mapbeam: true,
    chests: true,
    rumours: true,
    iconpatch: false
  };

  // ── 图标替换 ──
  //
  // 数据来自后端扫描结果：只包含当前客户端真实存在的路径。前端不做任何路径推断，
  // 写入时原样回传扫描得到的路径，后端会再校验一次。
  let iconPatchEntries = [];
  let iconPatchSkipped = 0;
  let iconSpriteCell = 48;
  let iconSpriteColumns = 32;
  let iconCurrentCategory = "全部";
  const ICON_CATEGORIES = ["全部", "通货", "血脉宝石", "终局地图", "维金", "其他"];

  function renderIconPatchTabs() {
    const tabs = element("iconPatchTabs");
    if (!tabs) return;
    tabs.innerHTML = ICON_CATEGORIES.map(cat => {
      const active = cat === iconCurrentCategory;
      const count = cat === "全部"
        ? iconPatchEntries.length
        : iconPatchEntries.filter(e => e.category === cat).length;
      return `<button class="btn-compact${active ? " active" : ""}" data-icon-cat="${cat}"
                style="${active ? "border-color: var(--accent); color: var(--accent);" : ""}">
                ${cat} ${count}
              </button>`;
    }).join("");
    tabs.querySelectorAll("[data-icon-cat]").forEach(btn => {
      btn.addEventListener("click", () => {
        iconCurrentCategory = btn.dataset.iconCat;
        renderIconPatchTabs();
        renderIconPatchList();
      });
    });
  }

  function getFilteredIconEntries() {
    const query = element("iconPatchSearch")?.value?.trim().toLowerCase() || "";
    return iconPatchEntries.filter(entry => {
      const matchCat = iconCurrentCategory === "全部" || entry.category === iconCurrentCategory;
      const matchQuery = !query || entry.fileName.toLowerCase().includes(query)
                      || entry.path.toLowerCase().includes(query);
      return matchCat && matchQuery;
    });
  }

  function renderIconPatchList() {
    const list = element("iconPatchList");
    if (!list) return;

    const items = getFilteredIconEntries();
    if (items.length === 0) {
      list.innerHTML = `<div style="padding: 20px; text-align: center; color: var(--text-muted); font-size: 11px;">
        当前筛选条件下没有图标。</div>`;
      updateIconPatchSummary();
      return;
    }

    list.innerHTML = items.map(entry => {
      const bgPos = `-${entry.spriteX}px -${entry.spriteY}px`;
      return `
        <label style="display: flex; align-items: center; gap: 8px; padding: 4px 6px;
                      border: 1px solid var(--border-color); border-radius: 4px; cursor: pointer;">
          <input type="checkbox" class="icon-entry-toggle" data-path="${entry.path}"
                 ${entry.enabled ? "checked" : ""} style="cursor: pointer;">
          <div style="width: ${iconSpriteCell}px; height: ${iconSpriteCell}px; flex-shrink: 0;
                      background-image: url(icons.sheet.webp);
                      background-position: ${bgPos};
                      background-repeat: no-repeat;
                      background-size: ${iconSpriteColumns * iconSpriteCell}px auto;
                      border-radius: 3px;"></div>
          <div style="flex: 1; min-width: 0;">
            <div style="font-size: 11px; color: var(--text); overflow: hidden;
                        text-overflow: ellipsis; white-space: nowrap;">${escapeHtml(entry.fileName)}</div>
            <div style="font-size: 10px; color: var(--text-muted);">
              ${escapeHtml(entry.category)} · 原 ${formatBytes(entry.originalSize)} → 新 ${formatBytes(entry.replacementSize)}
            </div>
          </div>
        </label>
      `;
    }).join("");

    list.querySelectorAll(".icon-entry-toggle").forEach(chk => {
      chk.addEventListener("change", e => {
        const entry = iconPatchEntries.find(x => x.path === e.target.dataset.path);
        if (entry) entry.enabled = e.target.checked;
        updateIconPatchSummary();
      });
    });

    updateIconPatchSummary();
  }

  function updateIconPatchSummary() {
    const summary = element("iconPatchSummary");
    if (!summary) return;
    const total = iconPatchEntries.length;
    const selected = iconPatchEntries.filter(e => e.enabled).length;
    const shown = getFilteredIconEntries().length;
    let text = `已选 ${selected} / 可用 ${total} 个`;
    if (shown !== total) text += `（当前筛选显示 ${shown} 个）`;
    if (iconPatchSkipped > 0) text += ` · ${iconPatchSkipped} 个不适用于当前版本已隐藏`;
    summary.innerText = text;

    const status = element("hubStatus-iconpatch");
    if (status) {
      status.innerText = total > 0 ? `● 可用 ${total} 个，已选 ${selected} 个` : "● 未读取客户端";
    }
  }

  function applyIconPatch() {
    // 总开关关闭时不允许写入。
    // 此前这里无条件把 moduleMasters.iconpatch 置 true 并硬编码 enabled:true，
    // 导致用户关掉总开关后点「应用当前工作台」照样写入客户端 —— 开关形同虚设。
    if (!moduleMasters.iconpatch) {
      showToast("图标替换总开关已关闭。如需应用，请先打开开关；若目标是还原，请点击「恢复原版图标」。");
      return;
    }

    const selected = iconPatchEntries.filter(e => e.enabled).map(e => e.path);
    if (selected.length === 0) {
      showToast("请先勾选要替换的图标");
      return;
    }

    showToast(`正在写入 ${selected.length} 个图标到游戏客户端，请稍候...`);
    post("applyIconPatch", { enabled: true, paths: selected });
  }

  /**
   * 把客户端图标还原成原始版本。
   *
   * 图标补丁直接覆盖 DDS，没有「取消勾选即还原」的语义，必须显式走这条
   * 路径把备份的官方原件写回。后端在没有备份时会明确拒绝并提示改用
   * 全量还原底包，不会伪装成功。
   */
  function restoreIconPatch() {
    if (!confirm("确定要把已替换的图标还原为客户端原始版本吗？")) return;
    showToast("正在还原原版图标...");
    post("applyIconPatch", { enabled: false, paths: [] });
  }

  // 当前工作台激活的模块 (默认 colormod)
  let currentActiveModule = "colormod";

  // 工作台切换
  function openWorkbench(moduleId) {
    currentActiveModule = moduleId;
    element("patcherHubView")?.classList.add("hidden");
    element("patcherWorkbenchView")?.classList.remove("hidden");

    document.querySelectorAll(".patcher-section").forEach(s => s.classList.add("hidden"));
    const targetSec = element("psec-" + moduleId);
    if (targetSec) targetSec.classList.remove("hidden");

    const titleEl = element("wbModuleTitle");
    if (titleEl) titleEl.innerText = MODULE_TITLES[moduleId] || "专注配置工作台";

    const selectEl = element("wbModuleSelect");
    if (selectEl) selectEl.value = moduleId;

    const btnSave = element("btnApplyCurrentWorkbench");
    if (btnSave) {
      btnSave.textContent = "💾 保存并同步当前功能";
      btnSave.title = `仅保存并同步【${MODULE_TITLES[moduleId] || "当前功能"}】配置到游戏`;
    }

    if (moduleId === "mtxpatch") {
      fetchMtxDatabase();
      renderMtxUi();
    }
  }

  function backToHub() {
    element("patcherWorkbenchView")?.classList.add("hidden");
    element("patcherHubView")?.classList.remove("hidden");
  }

  function toggleModuleMaster(moduleId, checked, event) {
    if (event) event.stopPropagation();
    moduleMasters[moduleId] = checked;
    const card = element("hcard-" + moduleId);
    if (card) {
      if (checked) card.classList.remove("card-disabled");
      else card.classList.add("card-disabled");
    }
    const statusEl = element("hubStatus-" + moduleId);
    if (statusEl) {
      statusEl.innerText = checked ? "● 已启用" : "○ 已关闭";
      statusEl.style.color = checked ? "#34d399" : "var(--text-muted, #94a3b8)";
    }
    const toggleInput = element("masterToggle-" + moduleId);
    if (toggleInput && toggleInput.checked !== checked) {
      toggleInput.checked = checked;
    }
  }

  // 状态反馈提示 (Toast)
  function showToast(msg, isError = false) {
    const feedbackBox = element("patcherFeedback");
    if (feedbackBox) {
      feedbackBox.textContent = msg;
      feedbackBox.className = "patcher-feedback " + (isError ? "error" : "info");
      setTimeout(() => {
        if (feedbackBox.textContent === msg) feedbackBox.className = "patcher-feedback";
      }, 4000);
    }
  }

  /**
   * 写剪贴板，返回 Promise。
   *
   * navigator.clipboard 在非安全上下文下不存在，权限被拒时 writeText 也会
   * reject。两种失败都必须让调用方知道，不能当成功处理。
   */
  function copyToClipboard(text) {
    if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") {
      return Promise.reject(new Error("当前环境不支持剪贴板写入，请手动选择文本复制"));
    }
    return navigator.clipboard.writeText(text);
  }

  function updateCardPreviewInfo() {
    const title = element('cardTitle');
    const sub = element('cardSubtitle');
    const props = element('cardBaseProps');
    const loot = element('lootLabelAffix');

    if (previewType === 'gear') {
      if (title) title.innerText = "法师之血 重革腰带";
      if (sub) sub.innerText = "传奇 重革腰带 (Mageblood)";
      if (props) props.innerHTML = `<span>需求等级: <strong>44</strong></span><span>品质: <strong style="color: #60a5fa;">+20%</strong></span>`;
      if (loot) loot.innerHTML = `<span style="color: #d4a754; font-weight: bold;">法师之血 [传奇]</span>`;
    } else if (previewType === 'tablet') {
      if (title) title.innerText = "远征日耀石板 (Sun Tablet)";
      if (sub) sub.innerText = "远征先祖石板";
      if (props) props.innerHTML = `<span>物品掉落数量: <strong>+15%</strong></span><span>遗迹数量: <strong>+2</strong></span>`;
      if (loot) loot.innerHTML = `<span style="color: #fbbf24; font-weight: bold;">远征日耀石板 [先祖]</span>`;
    } else if (previewType === 'endgame') {
      if (title) title.innerText = "终局征服者圣殿";
      if (sub) sub.innerText = "阶级: 16 终局首领区域";
      if (props) props.innerHTML = `<span>首领专属掉落: <strong>+100%</strong></span><span>附带专属侍从: <strong>4</strong></span>`;
      if (loot) loot.innerHTML = `<span style="color: #f43f5e; font-weight: bold;">终局征服者圣殿 [T16]</span>`;
    } else {
      if (title) title.innerText = "阿兹莫里山区 (Azmerian Ranges)";
      if (sub) sub.innerText = "阶级: 15 异界地图";
      if (props) props.innerHTML = `<span>物品稀有度: <strong style="color: var(--gold-primary, #d4a754);">稀有 (Rare)</strong></span><span>物品数量: <strong>+68%</strong></span>`;
      if (loot) loot.innerHTML = `<span style="color: #cbd5e1;">阿兹莫里山区 [T15]</span>`;
    }
  }

  // 智能词缀颜色兜底：杜绝纯黑死块，按类别提供鲜艳默认色
  function getAffixFallbackColor(item) {
    if (item.color && item.color !== "#000000" && item.color.toLowerCase() !== "#000" && item.color.toLowerCase() !== "black") {
      return item.color;
    }
    if (item.isDanger) return "#e64545"; // 高危致命红
    if (item.group === "地图" || item.group === "终局地图") return "#e64545";
    if (item.group === "石板") return "#eab308"; // 石板先祖金
    return "#38b469"; // 通用装备草绿
  }

  function escapeHtml(str) {
    if (!str) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // 词缀文本按染色规则动态着色：整句着色彩色加粗，仅数字着色高亮数值与占位符，未启用静音柔和
  function renderAffixTextHtml(displayName, item, effectiveColor) {
    const escaped = escapeHtml(displayName);
    if (!item.enabled) {
      return `<span style="color: var(--muted); opacity: 0.85;">${escaped}</span>`;
    }
    if (item.style === "Whole") {
      return `<span style="color: ${effectiveColor}; font-weight: 600;">${escaped}</span>`;
    }
    // NumberOnly: 把数值占位符(#, #%)、区间((15-20)%)及具体数字替换为彩色加粗，其余文字跟随系统主题色
    const numRegex = /((\+|-)?#%?|(\+|-)?\d+(?:\.\d+)?%?|\(\d+[-~]\d+\)%?|\(#[-~]#\)%?|\{\d+\}%?)/g;
    const highlighted = escaped.replace(numRegex, `<strong style="color: ${effectiveColor}; font-weight: 700;">$1</strong>`);
    return `<span style="color: var(--text); font-weight: 500;">${highlighted}</span>`;
  }

  // 1. 词缀染色表格渲染（轻量分页 + 纯中文 + 秒级渲染 + 真实色彩高亮）
  function renderTable() {
    const tbody = element("rulesTableBody");
    if (!tbody) return;
    tbody.innerHTML = "";

    if (colorModData.length === 0) {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td colspan="7" style="text-align: center; color: var(--text-muted); padding: 26px 12px; font-size: 11px;">
          暂无词缀规则。请点击上方【🎮 读取客户端词库】秒级提取当前游戏完整官方词库，或点击【✨ + 添加自定义规则】。
        </td>
      `;
      tbody.appendChild(tr);
      const countText = element("rulesCountText");
      if (countText) countText.innerText = "当前展示 0 条 (全库 0 条)";
      const pageIndicator = element("pageIndicator");
      if (pageIndicator) pageIndicator.innerText = "0 / 0";
      renderLiveCard();
      return;
    }

    const query = element("searchInput")?.value?.trim().toLowerCase() || "";
    const filtered = colorModData.filter(item => {
      const matchGroup = currentGroup === "全部" || item.group === currentGroup;
      const textToMatch = (item.text || item.displayName || "").toLowerCase();
      const tagToMatch = (item.tag || "").toLowerCase();
      const matchQuery = !query || textToMatch.includes(query) || tagToMatch.includes(query);
      return matchGroup && matchQuery;
    });

    // 关键优化：已开启规则与高危词缀优先置顶，首屏第一时间展示鲜明色彩
    filtered.sort((a, b) => {
      const aScore = (a.enabled ? 10 : 0) + (a.isDanger ? 5 : 0) + (a.isCustom ? 2 : 0);
      const bScore = (b.enabled ? 10 : 0) + (b.isDanger ? 5 : 0) + (b.isCustom ? 2 : 0);
      return bScore - aScore;
    });

    const totalPages = Math.max(1, Math.ceil(filtered.length / COLOR_MOD_PAGE_SIZE));
    if (colorModCurrentPage > totalPages) colorModCurrentPage = totalPages;
    if (colorModCurrentPage < 1) colorModCurrentPage = 1;

    const pagedItems = filtered.slice((colorModCurrentPage - 1) * COLOR_MOD_PAGE_SIZE, colorModCurrentPage * COLOR_MOD_PAGE_SIZE);

    const countText = element("rulesCountText");
    if (countText) {
      countText.innerText = `筛选 ${filtered.length} 条 (全库 ${colorModData.length} 条${isClientSynced ? ` | 客户端已挂载` : ''})`;
    }

    const pageIndicator = element("pageIndicator");
    if (pageIndicator) {
      pageIndicator.innerText = `${colorModCurrentPage} / ${totalPages}`;
    }

    const btnFirst = element("btnFirstPage");
    const btnPrev = element("btnPrevPage");
    const btnNext = element("btnNextPage");
    const btnLast = element("btnLastPage");
    if (btnFirst) btnFirst.disabled = colorModCurrentPage <= 1;
    if (btnPrev) btnPrev.disabled = colorModCurrentPage <= 1;
    if (btnNext) btnNext.disabled = colorModCurrentPage >= totalPages;
    if (btnLast) btnLast.disabled = colorModCurrentPage >= totalPages;

    if (pagedItems.length === 0) {
      const tr = document.createElement("tr");
      tr.innerHTML = `
        <td colspan="7" style="text-align: center; color: var(--text-muted); padding: 20px 12px; font-size: 11px;">
          在分类【${currentGroup}】下未找到匹配“${query}”的词缀。
        </td>
      `;
      tbody.appendChild(tr);
    } else {
      const fragment = document.createDocumentFragment();
      pagedItems.forEach(item => {
        const tr = document.createElement("tr");
        const displayName = item.text || item.displayName || item.id;
        const isDanger = item.isDanger;
        const effectiveColor = getAffixFallbackColor(item);
        if (!item.color || item.color === "#000000" || item.color.toLowerCase() === "#000") {
          item.color = effectiveColor;
        }

        const tagText = item.tag || (isDanger ? "高危" : (item.group || "通用"));
        const tagBadge = isDanger
          ? `<span style="font-size: 9px; padding: 1px 5px; background: rgba(239,68,68,0.15); color: #ef4444; border: 1px solid rgba(239,68,68,0.3); border-radius: 3px; font-weight: 600; white-space: nowrap;">高危</span>`
          : `<span style="font-size: 10px; color: ${item.enabled ? effectiveColor : 'var(--muted)'}; font-weight: 500; white-space: nowrap;">${escapeHtml(tagText)}</span>`;

        tr.innerHTML = `
          <td style="text-align: center;">
            <input type="checkbox" class="rule-enable-toggle" data-id="${item.id}" ${item.enabled ? 'checked' : ''} style="cursor: pointer;">
          </td>
          <td>
            <div style="display: flex; align-items: center; gap: 6px;">
              ${renderAffixTextHtml(displayName, item, effectiveColor)}
              ${item.isCustom ? '<span style="font-size: 9px; padding: 1px 4px; background: rgba(59,130,246,0.15); color: #3b82f6; border-radius: 3px; white-space: nowrap;">自定</span>' : ''}
            </div>
          </td>
          <td>
            <span style="font-size: 10px; padding: 2px 6px; background: var(--control); border: 1px solid var(--border-color); border-radius: 3px; color: var(--text-muted); white-space: nowrap;">
              ${escapeHtml(item.group || '通用')}
            </span>
          </td>
          <td style="text-align: center;">
            <input type="color" class="color-picker-input" data-id="${item.id}" value="${effectiveColor}" style="width: 24px; height: 22px; padding: 0; border: 1px solid var(--border-color); border-radius: 3px; background: none; cursor: pointer;">
          </td>
          <td>
            <select class="style-select preset-select" data-id="${item.id}" style="height: 22px; font-size: 10px; width: 72px; padding: 0 4px;">
              <option value="Whole" ${item.style === 'Whole' ? 'selected' : ''}>整句</option>
              <option value="NumberOnly" ${item.style === 'NumberOnly' ? 'selected' : ''}>仅数字</option>
            </select>
          </td>
          <td>
            ${tagBadge}
          </td>
          <td style="text-align: center;">
            <button class="btn-del-rule btn-compact" data-id="${item.id}" style="color: #ef4444; font-size: 11px; padding: 1px 5px; border-color: rgba(239,68,68,0.3);">✕</button>
          </td>
        `;
        fragment.appendChild(tr);
      });
      tbody.appendChild(fragment);
    }

    tbody.querySelectorAll(".rule-enable-toggle").forEach(chk => {
      chk.addEventListener("change", e => {
        const it = colorModData.find(x => x.id === e.target.dataset.id);
        if (it) {
          it.enabled = e.target.checked;
          commitView();          // 立刻落到当前预设，避免改动只停留在视图里
          renderTable();
          renderLiveCard();
        }
      });
    });
    tbody.querySelectorAll(".color-picker-input").forEach(cp => {
      cp.addEventListener("input", e => {
        const it = colorModData.find(x => x.id === e.target.dataset.id);
        if (it) {
          it.color = e.target.value;
          commitView();
          renderTable();
          renderLiveCard();
        }
      });
    });
    tbody.querySelectorAll(".style-select").forEach(sel => {
      sel.addEventListener("change", e => {
        const it = colorModData.find(x => x.id === e.target.dataset.id);
        if (it) {
          it.style = e.target.value;
          commitView();
          renderTable();
          renderLiveCard();
        }
      });
    });
    tbody.querySelectorAll(".btn-del-rule").forEach(btn => {
      btn.addEventListener("click", e => {
        colorModData = colorModData.filter(x => x.id !== e.target.dataset.id);
        commitView();
        renderTable();
        renderLiveCard();
      });
    });

    // 联动表头复选框（全选/未选/半选 indeterminate）
    const chkAll = element("chkSelectAllRules");
    if (chkAll) {
      const totalCount = filtered.length;
      const enabledCount = filtered.filter(x => x.enabled).length;
      if (totalCount === 0) {
        chkAll.checked = false;
        chkAll.indeterminate = false;
        chkAll.disabled = true;
      } else if (enabledCount === 0) {
        chkAll.checked = false;
        chkAll.indeterminate = false;
        chkAll.disabled = false;
      } else if (enabledCount === totalCount) {
        chkAll.checked = true;
        chkAll.indeterminate = false;
        chkAll.disabled = false;
      } else {
        chkAll.checked = false;
        chkAll.indeterminate = true;
        chkAll.disabled = false;
      }
    }

    renderLiveCard();
  }

  function renderLiveCard() {
    updateCardPreviewInfo();
    const box = element("cardAffixesBox");
    if (!box) return;
    box.innerHTML = "";

    // 确定当前预览类别对应的词缀分组
    let targetGroup = "地图";
    if (previewType === "gear") targetGroup = "通用";
    else if (previewType === "tablet") targetGroup = "石板";
    else if (previewType === "endgame") targetGroup = "终局地图";

    // 优先从真实词库 colorModData 中选取当前组别的词缀
    const groupRules = colorModData.filter(r => r.group === targetGroup);
    const activeInGroup = groupRules.filter(r => r.enabled);

    let displayList = [];
    if (activeInGroup.length > 0) {
      displayList = activeInGroup.slice(0, 5);
      // 如果已开启的少于 4 条，补充未开启的该组规则
      if (displayList.length < 4) {
        const inactive = groupRules.filter(r => !r.enabled);
        displayList = displayList.concat(inactive.slice(0, 4 - displayList.length));
      }
    } else if (groupRules.length > 0) {
      displayList = groupRules.slice(0, 4);
    } else {
      // 若当前组别尚无数据（例如未扫描时），提供典型真实的格式样例
      if (previewType === "gear") {
        displayList = [
          { text: "+(80-100) 最大生命", color: "#38b469", style: "Whole", enabled: true },
          { text: "+(12-16)% 全部元素抗性", color: "#d4a754", style: "Whole", enabled: true },
          { text: "+(25-30)% 攻击暴击伤害加成", color: "#4fa3e3", style: "NumberOnly", enabled: true },
          { text: "+(23-28)% 混沌抗性", color: "#a855f7", style: "Whole", enabled: true }
        ];
      } else if (previewType === "tablet") {
        displayList = [
          { text: "怪物免疫物理伤害", color: "#e64545", style: "Whole", enabled: true },
          { text: "怪物免疫混沌伤害", color: "#e64545", style: "Whole", enabled: true },
          { text: "+(40-50)% 终抗上限 (最大抗性)", color: "#d4a754", style: "Whole", enabled: true }
        ];
      } else {
        displayList = [
          { text: "怪物的暴击率增加 (200-300)%，暴击伤害加成 +(35-50)%", color: "#e64545", style: "Whole", enabled: true },
          { text: "玩家受到 (15-20)% 的物理伤害反射", color: "#e64545", style: "Whole", enabled: true },
          { text: "区域掉落物品数量增加 (60-80)%", color: "#d4a754", style: "NumberOnly", enabled: true },
          { text: "怪物发射 2 个额外投射物", color: "#e64545", style: "Whole", enabled: true }
        ];
      }
    }

    displayList.forEach(item => {
      const lineDiv = document.createElement("div");
      lineDiv.style.cssText = "font-size: 11px; line-height: 1.45; word-break: break-word;";

      const rawText = item.text;
      if (item.enabled) {
        if (item.style === "NumberOnly") {
          const highlighted = rawText.replace(/((\+|-)?#%?|(\+|-)?\d+(?:\.\d+)?%?|\(\d+[-~]\d+\)%?|\(#[-~]#\)%?|\{\d+\}%?)/g, `<span style="color: ${item.color}; font-weight: bold;">$1</span>`);
          lineDiv.innerHTML = `• ${highlighted}`;
        } else {
          lineDiv.innerHTML = `• <span style="color: ${item.color}; font-weight: 500;">${rawText}</span>`;
        }
      } else {
        lineDiv.innerHTML = `• <span style="color: #94a3b8;">${rawText}</span>`;
      }
      box.appendChild(lineDiv);
    });
  }

  // 2. 神图冲天光柱渲染
  function renderMapBeam() {
    const box = element("mapBeamListContainer");
    if (!box) return;
    box.innerHTML = "";
    MAP_BEAM_DATA.forEach(item => {
      const row = document.createElement("div");
      row.className = "patcher-item-row";
      row.innerHTML = `
        <div style="display: flex; align-items: center; gap: 8px;">
          <input type="checkbox" class="chk-beam-toggle" data-name="${item.name}" ${item.enabled ? 'checked' : ''} style="cursor: pointer;">
          <div>
            <strong style="color: var(--text); font-size: 11px;">${item.nameZh} (${item.name})</strong>
            <div style="color: var(--text-muted); font-size: 10px;">${item.desc}</div>
          </div>
        </div>
        <div style="display: flex; align-items: center; gap: 4px;">
          <button class="btn-beam-color btn-compact" data-name="${item.name}" data-color="Farudin" data-hex="#c9a84c" style="font-size: 9px; padding: 2px 5px; ${item.color === 'Farudin' ? 'border-color: #c9a84c; color: #c9a84c;' : ''}">金</button>
          <button class="btn-beam-color btn-compact" data-name="${item.name}" data-color="Vaal" data-hex="#e64545" style="font-size: 9px; padding: 2px 5px; ${item.color === 'Vaal' ? 'border-color: #e64545; color: #e64545;' : ''}">红</button>
          <button class="btn-beam-color btn-compact" data-name="${item.name}" data-color="Green" data-hex="#4c9a5f" style="font-size: 9px; padding: 2px 5px; ${item.color === 'Green' ? 'border-color: #4c9a5f; color: #4c9a5f;' : ''}">绿</button>
          <button class="btn-beam-color btn-compact" data-name="${item.name}" data-color="Yellow" data-hex="#e0b33c" style="font-size: 9px; padding: 2px 5px; ${item.color === 'Yellow' ? 'border-color: #e0b33c; color: #e0b33c;' : ''}">黄</button>
        </div>
      `;
      box.appendChild(row);
    });

    box.querySelectorAll(".chk-beam-toggle").forEach(chk => {
      chk.addEventListener("change", e => {
        const it = MAP_BEAM_DATA.find(x => x.name === e.target.dataset.name);
        if (it) it.enabled = e.target.checked;
      });
    });
    box.querySelectorAll(".btn-beam-color").forEach(btn => {
      btn.addEventListener("click", e => {
        const it = MAP_BEAM_DATA.find(x => x.name === e.target.dataset.name);
        if (it) {
          it.color = e.target.dataset.color;
          it.hex = e.target.dataset.hex;
          renderMapBeam();
          // 更新右侧视窗
          const core = element("beamCore");
          if (core) core.style.background = `linear-gradient(to top, ${it.hex} 0%, ${it.hex}99 40%, transparent 100%)`;
          const lbl = element("beamLabel");
          if (lbl) {
            lbl.style.color = it.hex;
            lbl.style.borderColor = it.hex;
            lbl.innerText = `${it.nameZh} (${it.name})`;
          }
        }
      });
    });
  }

  // 3. 宝箱掉落透视与规则驱动渲染
  function updateChestsBadge(synced, text) {
    const badgeText = element("chestsStatusText");
    const badgeDot = element("chestsStatusDot");
    if (badgeText) {
      badgeText.innerText = text;
      badgeText.style.color = synced ? "#34d399" : "var(--text-muted)";
    }
    if (badgeDot) {
      badgeDot.classList.toggle("synced", synced);
    }
  }

  function initChestsFromClient(clientChests) {
    if (!clientChests || clientChests.length === 0) return;
    CHESTS_DATA = clientChests.map(c => ({
      originalName: c.originalName,
      modifiedName: c.displayName || c.memo || c.originalName,
      desc: c.category ? `[${c.category}] 规则识别` : "宝箱增强",
      color: c.color ? (c.color.startsWith('#') || c.color.startsWith('rgb') ? c.color : `#${c.color}`) : "#eab308",
      enabled: c.enabled !== false,
      category: c.category || "其它",
      groupedRowIndices: c.groupedRowIndices || [c.rowIndex]
    }));
    updateChestsBadge(true, `客户端实际 (${CHESTS_DATA.length}类)`);
    renderChestsList();
  }

  function renderChestsList() {
    const box = element("chestsListContainer");
    if (!box) return;
    box.innerHTML = "";

    if (CHESTS_DATA.length === 0) {
      box.innerHTML = `
        <div style="text-align: center; color: var(--text-muted); padding: 18px 10px; font-size: 11px;">
          暂无宝箱数据。请点击右上角【🎮 读取客户端宝箱】从游戏客户端提取实时数据。
        </div>
      `;
      return;
    }

    CHESTS_DATA.forEach(c => {
      const item = document.createElement("div");
      item.className = "patcher-item-row";
      item.innerHTML = `
        <div style="display: flex; align-items: center; gap: 8px;">
          <input type="checkbox" class="chk-chest-toggle" data-name="${escapeHtml(c.originalName)}" ${c.enabled ? 'checked' : ''} style="cursor: pointer;">
          <div>
            <strong style="color: ${c.color}; font-size: 11px;">${escapeHtml(c.modifiedName)}</strong>
            <div style="color: var(--text-muted); font-size: 10px;">${escapeHtml(c.desc)} (${escapeHtml(c.originalName)})</div>
          </div>
        </div>
        <span style="font-size: 10px; color: ${c.enabled ? '#34d399' : 'var(--text-muted)'};">${c.enabled ? '已标注' : '原版'}</span>
      `;
      box.appendChild(item);
    });

    box.querySelectorAll(".chk-chest-toggle").forEach(chk => {
      chk.addEventListener("change", e => {
        const it = CHESTS_DATA.find(x => x.originalName === e.target.dataset.name);
        if (it) it.enabled = e.target.checked;
      });
    });
  }

  // 6. 异界传言表格渲染
  function renderRumoursTable() {
    const tbody = element("rumoursTbody");
    if (!tbody) return;
    tbody.innerHTML = "";
    const q = element("rumourSearch")?.value?.trim().toLowerCase() || "";

    const filtered = RUMOURS_DATA.filter(r =>
      !q ||
      (r.mapName && r.mapName.toLowerCase().includes(q)) ||
      (r.category && r.category.toLowerCase().includes(q)) ||
      (r.originalText && r.originalText.toLowerCase().includes(q)) ||
      (r.modifiedText && r.modifiedText.toLowerCase().includes(q))
    );

    if (filtered.length === 0) {
      const emptyRow = document.createElement("tr");
      emptyRow.innerHTML = `
        <td colspan="6" style="text-align: center; color: var(--text-muted); padding: 24px 10px; font-size: 11px;">
          ${RUMOURS_DATA.length === 0 ? "正在等待从游戏客户端读取异界传言数据..." : "未找到匹配的传言项"}
        </td>
      `;
      tbody.appendChild(emptyRow);
      return;
    }

    filtered.forEach(item => {
      const tr = document.createElement("tr");
      const categoryColor = item.category === "高危" || item.category === "差圖" || item.category === "差图" ? "#ef4444" :
                            item.category === "神圖" || item.category === "神图" ? "#eab308" :
                            item.category === "奇遇" ? "#a855f7" :
                            item.category === "機制" || item.category === "机制" ? "#e39eff" : "#3b82f6";

      tr.innerHTML = `
        <td style="text-align: center;">
          <input type="checkbox" class="chk-rumour-toggle" data-row="${item.rowIndex}" ${item.enabled ? 'checked' : ''} style="cursor: pointer;">
        </td>
        <td style="text-align: center; font-size: 10px; color: var(--text-muted); font-family: monospace;">
          #${item.rowIndex}
        </td>
        <td style="font-size: 11px; color: var(--text); font-weight: 500;">
          ${escapeHtml(item.mapName || "未知地图")}
        </td>
        <td style="text-align: center;">
          <span style="display: inline-block; font-size: 9px; padding: 1px 5px; border-radius: 3px; background: ${categoryColor}22; color: ${categoryColor}; border: 1px solid ${categoryColor}55;">
            ${escapeHtml(item.category || "常规")}
          </span>
        </td>
        <td style="color: var(--text-muted); font-size: 11px;">
          ${escapeHtml(item.originalText)}
        </td>
        <td>
          <input type="text" class="inp-rumour-modified" data-row="${item.rowIndex}" value="${escapeHtml(item.modifiedText)}" style="width: 100%; height: 22px; font-size: 11px; background: rgba(0,0,0,0.25); border: 1px solid var(--border-color); border-radius: 3px; padding: 0 6px; color: ${item.color || '#eab308'}; font-weight: 500;" />
        </td>
      `;
      tr.addEventListener("mouseenter", () => updateRumourSimCard(item));
      tbody.appendChild(tr);
    });

    tbody.querySelectorAll(".chk-rumour-toggle").forEach(chk => {
      chk.addEventListener("change", e => {
        const row = Number(e.target.dataset.row);
        const it = RUMOURS_DATA.find(x => x.rowIndex === row);
        if (it) it.enabled = e.target.checked;
      });
    });

    tbody.querySelectorAll(".inp-rumour-modified").forEach(inp => {
      inp.addEventListener("input", e => {
        const row = Number(e.target.dataset.row);
        const it = RUMOURS_DATA.find(x => x.rowIndex === row);
        if (it) {
          it.modifiedText = e.target.value;
          updateRumourSimCard(it);
        }
      });
    });

    if (filtered.length > 0) {
      updateRumourSimCard(filtered[0]);
    }
  }

  function updateRumourSimCard(item) {
    if (!item) return;
    const title = element("simRumourMapTitle");
    const preview = element("simRumourPreviewText");
    const tag = element("simRumourCategoryTag");
    if (title) title.innerText = item.mapName ? `${item.mapName} (行 #${item.rowIndex})` : `行 #${item.rowIndex}`;
    if (preview) {
      const rendered = (item.modifiedText || "").replace(/<rgb\((\d+),(\d+),(\d+)\)>\{([^}]+)\}/g, '<span style="color: rgb($1,$2,$3); font-weight: bold;">$4</span>');
      preview.innerHTML = rendered || escapeHtml(item.originalText || "");
      preview.style.color = item.color || "#eab308";
    }
    if (tag) tag.innerText = `🏷️ 规则分类：${item.category || "常规"}`;
  }

  // 7. 价格即时仿真
  function updatePriceSim() {
    const sep = element("priceSepSelect")?.value || " ";
    const simDiv = element("simPriceUnique");
    if (simDiv) simDiv.innerText = `法师之血 重革腰带${sep}[280D]`;
    const simDivine = element("simPriceDivine");
    if (simDivine) simDivine.innerText = `神圣石${sep}[400E]`;

    // 低于门槛的示例必须反映当前门槛值，否则用户改了门槛这行字仍不变，
    // 会误以为设置没生效。
    const simSmall = element("simPriceSmall");
    if (simSmall) {
      const rawMin = element("priceFilterMin")?.value ?? "1";
      const min = rawMin === "" ? 1 : Number(rawMin);
      const effective = Number.isFinite(min) && min >= 0 ? min : 1;
      simSmall.innerText = `重铸石 (低于 ${effective}E 门槛，已隐藏物价后缀)`;
    }
  }

  const PRICE_CATEGORY_INPUTS = {
    chkPriceCurrency: 'Currency',
    chkPriceLineage: 'LineageSupportGems',
    chkPriceRunes: 'Runes',
    chkPriceEssences: 'Essences',
    chkPriceSoulCores: 'SoulCores',
    chkPriceUncutGems: 'UncutGems',
    chkPriceIdols: 'Idols',
    chkPriceExpedition: 'Expedition',
    chkPriceBreach: 'Breach',
    chkPriceDelirium: 'Delirium',
    chkPriceRitual: 'Ritual',
    chkPriceFragments: 'Fragments',
    chkPriceAbyss: 'Abyss',
    chkPriceVerisium: 'Verisium'
  };

  const CATEGORY_NAMES_ZH = {
    currency: '基础通货',
    lineagesupportgems: '传承血脉',
    runes: '符文',
    essences: '精华',
    soulcores: '灵魂核心',
    uncutgems: '未切割宝石',
    idols: '神像',
    expedition: '远征',
    breach: '裂隙',
    delirium: '谵妄',
    ritual: '仪式',
    fragments: '碎片',
    abyss: '深渊',
    verisium: '维利席姆'
  };

  function collectPriceCategories() {
    return Object.entries(PRICE_CATEGORY_INPUTS)
      .filter(([id]) => element(id)?.checked)
      .map(([, category]) => category);
  }

  function applyPriceSettings(settings) {
    if (!settings) return;
    const minInput = element('priceFilterMin');
    if (minInput && settings.pricePatchMinExalted != null && Number.isFinite(Number(settings.pricePatchMinExalted))) {
      minInput.value = Number(settings.pricePatchMinExalted);
    }
    const stripInput = element('chkPriceStripExisting');
    if (stripInput && typeof settings.pricePatchStripExisting === 'boolean') {
      stripInput.checked = settings.pricePatchStripExisting;
    }
    // 分隔符必须回显，否则用户改完重启会看到下拉框退回空格默认值。
    const sepSelect = element('priceSepSelect');
    if (sepSelect && typeof settings.pricePatchSeparator === 'string') {
      const saved = settings.pricePatchSeparator;
      const hasOption = Array.from(sepSelect.options).some(o => o.value === saved);
      if (hasOption) sepSelect.value = saved;
    }
    const categories = Array.isArray(settings.pricePatchCategories)
      ? settings.pricePatchCategories
      : null;
    Object.entries(PRICE_CATEGORY_INPUTS).forEach(([id, category]) => {
      const input = element(id);
      if (!input) return;
      input.checked = categories === null
        || categories.some(value => String(value).toLowerCase() === category.toLowerCase());
    });
  }

  // 真实读取客户端词库 (打通后端)
  function scanClientDictionary() {
    const spinner = element("scanSpinner");
    const btnText = element("scanBtnText");
    const btn = element("btnScanClient");

    spinner?.classList.remove("hidden");
    if (btnText) btnText.innerText = "正在读取客户端...";
    if (btn) btn.style.opacity = "0.8";

    // 向 C# 后端发送真实扫描命令
    post('scanClientDictionary');
  }

  // ── 单模块独立保存与同步（严格隔离，仅保存当前功能） ──
  function applyColorMod() {
    lastSnapshot = JSON.parse(JSON.stringify(colorModData));
    // 先把当前编辑提交到预设配置，确保写入客户端的内容与界面一致
    commitView();
    showToast("正在将【词缀多色彩染色】配置写入 PoE2 游戏客户端，请稍候...");
    post('applyColorModPatch', {
      enabled: moduleMasters.colormod,
      style: element('patcherColorModStyle')?.value || 'Whole',
      // 只提交启用中的条目。样式与颜色由前端算好，后端仅负责写入 CSD。
      rules: colorModData.filter(r => r.enabled).map(r => ({
        id: r.id,
        group: r.group || '通用',
        color: r.color || '#ffffff',
        style: r.style === 'NumberOnly' ? 'NumberOnly' : 'Whole',
        tag: r.tag || '',
        isDanger: !!r.isDanger,
        enabled: true
      }))
    });
  }

  function applyMapBeam() {
    showToast("正在将【神图冲天光柱】配置写入 PoE2 游戏客户端，请稍候...");
    post('applyMapBeamPatch', {
      enabled: moduleMasters.mapbeam,
      targets: MAP_BEAM_DATA.map(x => ({
        mapName: x.name,
        displayName: x.nameZh,
        color: x.color,
        enabled: x.enabled
      }))
    });
  }

  function applyChests() {
    showToast("正在将【宝箱掉落透视标注】配置写入 PoE2 游戏客户端，请稍候...");
    post('applyChestPatch', {
      enabled: moduleMasters.chests,
      items: CHESTS_DATA.map(x => ({
        originalName: x.originalName,
        modifiedName: x.modifiedName,
        enabled: x.enabled
      }))
    });
  }

  function applyRumours() {
    showToast("正在将【异界地图传言标注】配置写入 PoE2 游戏客户端，请稍候...");
    post('applyRumourPatch', {
      enabled: moduleMasters.rumours,
      items: RUMOURS_DATA.map(x => ({
        rowIndex: x.rowIndex,
        mapName: x.mapName || "",
        category: x.category || "常规",
        color: x.color || "#ffffff",
        originalText: x.originalText || "",
        modifiedText: x.modifiedText || "",
        enabled: !!x.enabled
      }))
    });
  }

  let pendingPricePreviewToken = null;

  function buildPricePayload() {
    const categories = collectPriceCategories();
    if (categories.length === 0) {
      showToast("请至少选择一个物价分类。");
      return null;
    }
    const rawMin = element('priceFilterMin')?.value ?? '1';
    const parsedMin = rawMin === '' ? 1 : Number(rawMin);
    const minExalted = Number.isFinite(parsedMin) && parsedMin >= 0 ? parsedMin : 1;
    return {
      mode: 'append',
      separator: element('priceSepSelect')?.value || ' ',
      minExalted,
      unitMode: 'auto',
      stripExisting: element('chkPriceStripExisting')?.checked !== false,
      categories
    };
  }

  let currentPreviewItems = [];

  function hidePricePreview() {
    pendingPricePreviewToken = null;
    currentPreviewItems = [];
    element('pricePatchPreview')?.classList.add('hidden');
  }

  /**
   * 国服客户端原生即简体中文，汉化补丁只会用内置词典覆盖官方译文，因此在 UI 层禁用。
   * 后端 TranslationPatchService 同样会拒绝执行，这里只负责给出直观反馈。
   */
  function applyTranslationAvailability(state) {
    const disabled = Boolean(state.isChina);
    const card = element('hcard-translation');
    const button = element('btnApplyTranslationPatch');
    const status = element('hubStatus-translation');

    if (button) {
      button.disabled = disabled;
      button.title = disabled
        ? '国服客户端原生已是简体中文，无需汉化'
        : '独立注入官方简体中文，不与字体、物价联动';
    }
    if (card) {
      card.style.opacity = disabled ? '0.55' : '';
    }
    if (status && disabled) {
      status.textContent = '● 国服原生简中，无需汉化';
    }
  }

  /// 参数变更导致已确认的预览计划失效时调用：丢弃 token 并给出提示。
  function invalidatePendingPricePreview() {
    if (!pendingPricePreviewToken) return;
    hidePricePreview();
    showToast("物价参数已变更，请重新点击【读取客户端并预览】");
  }

  function applyPrice() {
    if (pendingPricePreviewToken) {
      const token = pendingPricePreviewToken;
      hidePricePreview();
      showToast("正在将已确认物价写入客户端...");
      post('confirmCurrentPricesPatch', { token });
      const btnSave = element("btnApplyCurrentWorkbench");
      if (btnSave) {
        btnSave.textContent = "💾 保存并同步当前功能";
        btnSave.style.background = "";
      }
      return;
    }

    const payload = buildPricePayload();
    if (!payload) return;
    hidePricePreview();
    showToast("正在将物价写入客户端，请稍候...");
    post('applyCurrentPricesPatch', payload);
  }

  function resolveItemCategory(item) {
    const rawCat = (item.category || item.Category || "").trim();
    if (rawCat) return rawCat.toLowerCase();
    const meta = (item.metadataPath || item.MetadataPath || "").toLowerCase();
    if (meta.includes("lineage")) return "lineagesupportgems";
    if (meta.includes("gem")) return "uncutgems";
    if (meta.includes("rune")) return "runes";
    if (meta.includes("soulcore")) return "soulcores";
    if (meta.includes("essence")) return "essences";
    if (meta.includes("idol")) return "idols";
    if (meta.includes("expedition")) return "expedition";
    if (meta.includes("breach")) return "breach";
    if (meta.includes("delirium")) return "delirium";
    if (meta.includes("ritual") || meta.includes("omen")) return "ritual";
    if (meta.includes("fragment")) return "fragments";
    if (meta.includes("abyss")) return "abyss";
    if (meta.includes("verisium")) return "verisium";
    const orig = (item.originalName || item.OriginalName || "");
    if (orig.includes("血脉") || orig.includes("传承")) return "lineagesupportgems";
    if (orig.includes("宝石")) return "uncutgems";
    if (orig.includes("符文")) return "runes";
    if (orig.includes("精华")) return "essences";
    if (orig.includes("核心")) return "soulcores";
    if (orig.includes("神像")) return "idols";
    return "currency";
  }

  function previewPrice() {
    const payload = buildPricePayload();
    if (!payload) return;
    hidePricePreview();
    element("pricePreviewSpinner")?.classList.remove("hidden");
    const btnText = element("pricePreviewBtnText");
    if (btnText) btnText.innerText = "🎮 正在读取客户端...";
    showToast("正在读取客户端实际数据表并匹配物价，请稍候...");
    post('previewCurrentPricesPatch', payload);
  }

  function renderPricePreviewTable(filterQuery = "") {
    const tbody = element("pricePreviewTableBody");
    const countEl = element("pricePreviewFilteredCount");
    const matchedEl = element("pricePreviewMatched");
    if (!tbody) return;
    tbody.innerHTML = "";

    const activeCategories = collectPriceCategories();
    const activeLower = activeCategories.map(c => c.toLowerCase());
    const q = filterQuery.trim().toLowerCase();
    const filtered = currentPreviewItems.filter(item => {
      const cat = resolveItemCategory(item);
      if (activeLower.length > 0 && !activeLower.some(c => c === cat || cat.includes(c) || c.includes(cat))) {
        return false;
      }
      if (!q) return true;
      const orig = item.originalName || item.OriginalName || "";
      const pr = item.price || item.Price || "";
      const nw = item.newName || item.NewName || "";
      const mp = item.metadataPath || item.MetadataPath || "";
      return orig.toLowerCase().includes(q)
        || pr.toLowerCase().includes(q)
        || nw.toLowerCase().includes(q)
        || mp.toLowerCase().includes(q);
    });

    if (countEl) {
      countEl.textContent = `显示 ${filtered.length} / ${currentPreviewItems.length} 条`;
    }
    if (matchedEl) {
      matchedEl.textContent = filtered.length;
    }

    if (filtered.length === 0) {
      const tr = document.createElement("tr");
      tr.innerHTML = `<td colspan="6" style="text-align: center; color: var(--text-muted); padding: 18px 6px; font-size: 11px;">
        ${currentPreviewItems.length === 0 ? "未匹配到需要注入物价的物品" : "未找到匹配当前分类或搜索条件的预览物品"}
      </td>`;
      tbody.appendChild(tr);
      return;
    }

    filtered.forEach((it, idx) => {
      const tr = document.createElement("tr");
      const cat = resolveItemCategory(it);
      const catZh = CATEGORY_NAMES_ZH[cat] || cat;
      const orig = it.originalName || it.OriginalName || "";
      const pr = it.price || it.Price || "";
      const nw = it.newName || it.NewName || "";
      const isCompacted = it.compacted !== undefined ? it.compacted : it.Compacted;
      const statusBadge = isCompacted
        ? `<span style="font-size: 9px; padding: 1px 4px; border-radius: 3px; background: rgba(234,179,8,0.2); color: #eab308; border: 1px solid rgba(234,179,8,0.4);">截断</span>`
        : `<span style="font-size: 9px; padding: 1px 4px; border-radius: 3px; background: rgba(52,211,153,0.15); color: #34d399; border: 1px solid rgba(52,211,153,0.3);">正常</span>`;

      tr.innerHTML = `
        <td style="text-align: center; color: var(--text-muted); font-size: 10px;">${idx + 1}</td>
        <td style="font-size: 11px;"><span style="font-size: 10px; padding: 1px 5px; border-radius: 3px; background: rgba(255,255,255,0.06); color: var(--text-muted); border: 1px solid var(--border-color);">${catZh}</span></td>
        <td style="font-size: 11px; color: var(--text); font-weight: 500;">${escapeHtml(orig)}</td>
        <td style="text-align: center; font-size: 11px; color: var(--gold-primary); font-weight: bold;">${escapeHtml(pr)}</td>
        <td style="font-size: 11px; color: #60a5fa;">${escapeHtml(nw)}</td>
        <td style="text-align: center;">${statusBadge}</td>
      `;
      tbody.appendChild(tr);
    });
  }

  function renderPricePreview(preview) {
    element("pricePreviewSpinner")?.classList.add("hidden");
    const btnText = element("pricePreviewBtnText");
    if (btnText) btnText.innerText = "🎮 读取客户端并预览";
    if (!preview || !preview.success) return;
    pendingPricePreviewToken = preview.token;
    currentPreviewItems = Array.isArray(preview.items) ? preview.items : (Array.isArray(preview.Items) ? preview.Items : []);
    const panel = element('pricePatchPreview');
    if (!panel) return;
    panel.classList.remove('hidden');
    element('pricePreviewCandidate').textContent = preview.candidateCount ?? 0;
    element('pricePreviewMatched').textContent = preview.matchedCount ?? 0;
    element('pricePreviewUnmatched').textContent = preview.unmatchedCount ?? 0;
    element('pricePreviewCompacted').textContent = preview.compactedNames ?? 0;
    const warnings = Array.isArray(preview.warnings) ? preview.warnings.filter(Boolean) : [];
    element('pricePreviewWarnings').textContent = warnings.length > 0
      ? `注意：${warnings.join('；')}`
      : '';

    // 顶部保存按钮直接高亮联动，提示用户可在顶部一键写入
    const btnSave = element("btnApplyCurrentWorkbench");
    if (btnSave && currentActiveModule === "price") {
      btnSave.textContent = "🚀 确认并写入客户端";
      btnSave.title = "预览已完成，点击立即将已确认物价写入客户端游戏文件";
      btnSave.style.background = "#059669";
    }

    const searchInput = element('pricePreviewSearch');
    if (searchInput) searchInput.value = "";
    renderPricePreviewTable("");
  }

  // ── 字体预设管理与成熟默认锁定机制 ──
  let currentFontPresetId = "default";
  let customFontPresets = {};

  function renderFontPresetSelect() {
    const sel = element("fontPresetSelect");
    if (!sel) return;
    sel.innerHTML = '<option value="default">🌟 成熟推荐预设 (官方标准兜底)</option>';
    Object.values(customFontPresets).forEach(p => {
      const opt = document.createElement("option");
      opt.value = p.id;
      opt.text = `💾 ${p.name}`;
      if (p.id === currentFontPresetId) opt.selected = true;
      sel.appendChild(opt);
    });
    sel.value = currentFontPresetId;

    const btnDel = element("btnDeleteFontPreset");
    if (btnDel) {
      if (currentFontPresetId === "default") btnDel.classList.add("hidden");
      else btnDel.classList.remove("hidden");
    }
  }

  function onFontPresetChange(newId) {
    currentFontPresetId = newId;
    if (newId === "default") {
      setSelectValue("fontSizeDropItemSelect", "0");
      setSelectValue("fontSizeGeneralSelect", "0");
      setSelectValue("fontSizeTitleSelect", "0");
      setSelectValue("fontSizeTinySelect", "0");
      setSelectValue("fontSizeDeltaSelect", "0");
      if (element("chkFontBold")) element("chkFontBold").checked = false;
      showToast("已恢复为【🌟 成熟推荐预设 (官方标准兜底)】");
    } else if (customFontPresets[newId]) {
      const p = customFontPresets[newId];
      if (p.fontFamily && element("fontSelectDropdown")) element("fontSelectDropdown").value = p.fontFamily;
      setSelectValue("fontSizeDropItemSelect", String(p.dropItemSizeDelta ?? 0));
      setSelectValue("fontSizeGeneralSelect", String(p.generalTextSizeDelta ?? 0));
      setSelectValue("fontSizeTitleSelect", String(p.titleSizeDelta ?? 0));
      setSelectValue("fontSizeTinySelect", String(p.tinyBadgeSizeDelta ?? 0));
      setSelectValue("fontSizeDeltaSelect", String(p.globalSizeDelta ?? 0));
      if (element("chkFontBold")) element("chkFontBold").checked = !!p.isBold;
      showToast(`已加载自定义预设【${p.name}】`);
    }
    updateFontPreview();
    renderFontPresetSelect();
  }

  function setSelectValue(id, val) {
    const el = element(id);
    if (el) el.value = val;
  }

  function saveCustomFontPreset(name) {
    const trimmed = name?.trim();
    if (!trimmed) return "预设名称不能为空";
    if (trimmed === "默认预设" || trimmed.includes("官方标准") || trimmed.includes("成熟推荐") || trimmed.toLowerCase() === "default") {
      return "成熟推荐预设为官方标准兜底方案，禁止覆盖！请使用其他名称保存为自定义预设。";
    }
    const presetId = "font_preset_" + Date.now();
    customFontPresets[presetId] = {
      id: presetId,
      name: trimmed,
      fontFamily: element("fontSelectDropdown")?.value || "Microsoft YaHei",
      globalSizeDelta: parseInt(element("fontSizeDeltaSelect")?.value, 10) || 0,
      dropItemSizeDelta: parseInt(element("fontSizeDropItemSelect")?.value, 10) || 0,
      generalTextSizeDelta: parseInt(element("fontSizeGeneralSelect")?.value, 10) || 0,
      titleSizeDelta: parseInt(element("fontSizeTitleSelect")?.value, 10) || 0,
      tinyBadgeSizeDelta: parseInt(element("fontSizeTinySelect")?.value, 10) || 0,
      isBold: !!element("chkFontBold")?.checked
    };
    currentFontPresetId = presetId;
    renderFontPresetSelect();
    showToast(`🎉 已成功保存自定义排版预设【${trimmed}】！`);
    return null;
  }

  function deleteCurrentCustomFontPreset() {
    if (currentFontPresetId === "default") return;
    const p = customFontPresets[currentFontPresetId];
    if (!p) return;
    if (confirm(`确定要删除自定义排版预设【${p.name}】吗？删除后将无法恢复。`)) {
      delete customFontPresets[currentFontPresetId];
      currentFontPresetId = "default";
      onFontPresetChange("default");
      showToast(`已删除自定义预设【${p.name}】，并还原为成熟官方标准`);
    }
  }

  function updateFontPreview() {
    const card = element("fontPreviewCard");
    const dropdown = element("fontSelectDropdown");
    const deltaSelect = element("fontSizeDeltaSelect");
    const chkBold = element("chkFontBold");
    const boldText = element("boldStatusText");

    const dropItemDelta = parseInt(element("fontSizeDropItemSelect")?.value, 10) || 0;
    const generalDelta = parseInt(element("fontSizeGeneralSelect")?.value, 10) || 0;
    const titleDelta = parseInt(element("fontSizeTitleSelect")?.value, 10) || 0;
    const tinyDelta = parseInt(element("fontSizeTinySelect")?.value, 10) || 0;

    const font = dropdown?.value || "Microsoft YaHei";
    const delta = deltaSelect ? parseInt(deltaSelect.value, 10) || 0 : 0;
    const isBold = chkBold ? !!chkBold.checked : false;

    if (boldText) {
      boldText.innerText = isBold ? "已加粗 (Weight 700)" : "未加粗";
      boldText.style.color = isBold ? "#34d399" : "var(--text-muted)";
    }

    // 1. 地面掉落标签联动
    const elDrop = element("simDropItemPreview");
    if (elDrop) {
      elDrop.style.fontFamily = `"${font}", sans-serif`;
      elDrop.style.fontWeight = isBold ? "700" : "bold";
      const dropScale = 13 + (dropItemDelta * 1.2) + (delta * 0.8);
      elDrop.style.fontSize = `${Math.max(9, dropScale)}px`;
    }

    // 2. 微小数值角标联动
    const elTiny = element("simTinyBadgePreview");
    if (elTiny) {
      elTiny.style.fontFamily = `"${font}", sans-serif`;
      const tinyScale = 10 + (tinyDelta * 1.0) + (delta * 0.5);
      elTiny.style.fontSize = `${Math.max(8, tinyScale)}px`;
    }

    // 3. 悬浮标题联动
    const elTitle = element("simTitlePreview");
    if (elTitle) {
      elTitle.style.fontFamily = `"${font}", sans-serif`;
      elTitle.style.fontWeight = isBold ? "700" : "bold";
      const titleScale = 13 + (titleDelta * 1.2) + (delta * 0.8);
      elTitle.style.fontSize = `${Math.max(10, titleScale)}px`;
    }

    // 4. 正文描述词缀联动
    const elGen = element("simGeneralPreview");
    if (elGen) {
      elGen.style.fontFamily = `"${font}", sans-serif`;
      elGen.style.fontWeight = isBold ? "600" : "normal";
      const genScale = 11.5 + (generalDelta * 1.0) + (delta * 0.8);
      elGen.style.fontSize = `${Math.max(9, genScale)}px`;
    }

    // 5. 卡片全局响应
    if (card) {
      card.style.fontFamily = `"${font}", sans-serif`;
    }
  }

  function applyFontMod() {
    showToast("正在将【成熟游戏排版与细分规则】写入 PoE2 游戏客户端，请稍候...");
    const fontDropdown = element("fontSelectDropdown");
    const fontName = fontDropdown?.value || "Microsoft YaHei UI";
    const deltaSelect = element("fontSizeDeltaSelect");
    const delta = deltaSelect ? parseInt(deltaSelect.value, 10) || 0 : 0;
    const chkBold = element("chkFontBold");
    const isBold = chkBold ? !!chkBold.checked : false;

    const dropItem = parseInt(element("fontSizeDropItemSelect")?.value, 10) || 0;
    const general = parseInt(element("fontSizeGeneralSelect")?.value, 10) || 0;
    const title = parseInt(element("fontSizeTitleSelect")?.value, 10) || 0;
    const tiny = parseInt(element("fontSizeTinySelect")?.value, 10) || 0;

    post('applyFontPatch', {
      fontFamily: fontName,
      fontSizeDelta: delta,
      isBold: isBold,
      dropItemSizeDelta: dropItem,
      generalTextSizeDelta: general,
      titleSizeDelta: title,
      tinyBadgeSizeDelta: tiny,
      presetId: currentFontPresetId,
      customPresets: Object.values(customFontPresets),
      applyPresetScales: true
    });
  }

  // 严格仅限于当前激活功能保存与同步
  function saveAndApplyCurrentModule() {
    switch (currentActiveModule) {
      case "colormod":
        applyColorMod();
        break;
      case "mapbeam":
        applyMapBeam();
        break;
      case "chests":
        applyChests();
        break;
      case "rumours":
        applyRumours();
        break;
      case "fontmod":
        applyFontMod();
        break;
      case "price":
        applyPrice();
        break;
      case "iconpatch":
        applyIconPatch();
        break;
      case "effectpatch":
        applyAllEffectSettings();
        break;
      default:
        console.warn("未知当前功能模块:", currentActiveModule);
        break;
    }
  }

  // ── 特效与性能优化 (EffectPatch) 状态与操作 ──
  let selectedEffectRules = new Set();
  let selectedBlockedSpells = new Set();
  let spellIncludeAudio = true;
  let spellSearchKeyword = "";
  let spellFilterOnlyChecked = false;

  const POE2_SPELL_OPTIONS = [
    { name: "霹雳闪", value: "monk_lightningstrike" },
    { name: "欺诈者碎片", value: "trickstershard" },
    { name: "爆灵术", value: "witch_detonate_dead|detonate_dead_v2|detonate_dead" },
    { name: "各类冰霜碎片", value: "cold_freezing_shard" },
    { name: "风雷钟", value: "monk_bell|monk_tempestbell" },
    { name: "爆破战矛", value: "spear_explosive_spear|spear_mine" },
    { name: "导引陨星神龛/陨石", value: "ShrineFireDaemon|meteor|meteorshrine|meteor_shower" },
    { name: "枪林矛阵", value: "spear_spearfield" },
    { name: "雷弹", value: "lightning_lightningbolt|shock_lightningbolt" },
    { name: "冰裂掌", value: "monk_shatteringpalm" },
    { name: "午夜天顶", value: "crystal_bloom" },
    { name: "苦涩死亡", value: "bitterdead" },
    { name: "门徒-纳薇拉相关效果", value: "djinn_ground_burst|ChilledGround|SorceressWaterDjinnChiilldedGroundBurst|djinn_water" },
    { name: "门徒-凯拉里相关效果", value: "djinn_sand" },
    { name: "门徒-鲁詹相关效果", value: "djinn_fire" },
    { name: "旋风击", value: "monk_whirlingassault" },
    { name: "元素之章", value: "berekthetaming" },
    { name: "旋龙（旋风）", value: "spear_tornadoes|spear_whirlwind" },
    { name: "回旋挥斩", value: "spear_whirlingslash|spear_whirlingslash_voodoo" },
    { name: "阴森之柱(只保留柱子)", value: "pillars_emerge|pillars_wave|pillars_explosion" },
    { name: "阴森之柱(全部去除)", value: "grim_pillars|grimpillars" },
    { name: "黏油炮弹/龙喷", value: "oil_barrage|oilbarrage|WyvernOilBarrage|OilGround|Oil_ground|wyvern" },
    { name: "复仇之灵", value: "minion_instability" },
    { name: "践踏之鞋子", value: "overkill_burst" },
    { name: "盾墙", value: "warrior_barricade" },
    { name: "火球", value: "fire_fireball" },
    { name: "爆炸射击（弩）", value: "explosivearrow|crossbow_incendiary" },
    { name: "爆炸箭矢（弓）", value: "bow_detonating_arrow" },
    { name: "冰霜射击", value: "lightning_arrow|bow_ice_shot" },
    { name: "闪电箭矢", value: "lightning_arrow|(enchantment_skills/arrows/lightning)|bow_electrocuting_rod|bow_lightning_arrow" },
    { name: "唤雷箭", value: "bow_shocking_arrow|lightning_storm_call" },
    { name: "闪电传送", value: "lightning_warp" },
    { name: "彗星", value: "cold_comet" },
    { name: "集束弹", value: "crossbow_cluster_grenade" },
    { name: "瓦斯榴弹", value: "crossbow_toxic_grenade" },
    { name: "闪光榴弹", value: "crossbow_stungrenade" },
    { name: "高爆榴弹", value: "crossbow_explosivegrenade" },
    { name: "黏油榴弹", value: "crossbow_oilgrenade" },
    { name: "雷霆之杖", value: "bow_lightning_rod_rain" },
    { name: "燃烧地面", value: "grd_burn" },
    { name: "冰地面", value: "grd_chill" },
    { name: "感电地面", value: "(ground_effect.*(lightning|shocking))|grd_shock" },
    { name: "各种地面", value: "grd_zones|burned_ground|chilled_ground|shocked_ground|ground_effects_v3|ground_effects" },
    { name: "冰墙", value: "frostwall" },
    { name: "冰霜新星", value: "icenova" },
    { name: "电矛", value: "spear_lightningspear" },
    { name: "电球", value: "lightning_spark" },
    { name: "烈焰之墙", value: "flame_wall" },
    { name: "各种诅咒", value: "curses_02" },
    { name: "各种捷", value: "herald" },
    { name: "魔侍焚焰使", value: "poe2_skill:Arsonist" },
    { name: "震波图腾", value: "poe2_skill:Shockwave_Totem" },
    { name: "震地", value: "poe2_skill:Earthquake" },
    { name: "瓦斯之箭", value: "poe2_skill:Gas_Arrow" },
    { name: "磁性齐射", value: "poe2_skill:Magnetic_Salvo" },
    { name: "天雷之珠", value: "poe2_skill:Ball_Lightning" },
    { name: "彗星落（保留球体）", value: "poe2_skill:Comet_With_Ball" },
    { name: "降解", value: "poe2_skill:Decompose" },
    { name: "燃烧铭文", value: "poe2_skill:Burning_Inscription" },
    { name: "凛冬之眼", value: "poe2_skill:Eye_of_Winter" },
    { name: "画地为牢", value: "poe2_skill:Bone_Cage" },
    { name: "魔侍神射手", value: "poe2_skill:Skeletal_Sniper" },
    { name: "烈炎风暴", value: "poe2_skill:Firestorm" },
    { name: "旋风投枪", value: "poe2_skill:Whirlwind_Lance" },
    { name: "火力弩炮", value: "poe2_skill:Artillery_Ballista" },
    { name: "攻城炮台", value: "poe2_skill:Siege_Ballista" },
    { name: "顽疾", value: "poe2_skill:Contagion" },
    { name: "佣兵弩（共用特效）", value: "poe2_skill:Mercenary_Crossbow" },
    { name: "电涌榴弹", value: "poe2_skill:Voltaic_Grenade" },
    { name: "寒冰弹", value: "poe2_skill:Frostbolt" },
    { name: "寒冰弹（保留球体）", value: "poe2_skill:Frostbolt_With_Ball" },
    { name: "大地震击", value: "poe2_skill:Sunder" },
    { name: "魔侍风暴法师", value: "poe2_skill:Skeletal_Storm_Mage" },
    { name: "武器灌注与充能", value: "poe2_skill:Infuse_Weapon" },
    { name: "威能法印", value: "poe2_skill:Sigil_of_Power" },
    { name: "冰川长枪", value: "poe2_skill:Glacial_Lance" },
    { name: "火山裂缝", value: "poe2_skill:Volcanic_Fissure" },
    { name: "强能狂热者", value: "poe2_skill:Powered_Zealot" },
    { name: "瓦尔军卫", value: "poe2_skill:Vaal_Guard" },
    { name: "冰封颚骨", value: "poe2_skill:Frozen_Mandibles" },
    { name: "掌旗狂热者", value: "poe2_skill:Bannerbearing_Zealot" },
    { name: "封冻枢纽", value: "poe2_skill:Frost_Nexus" },
    { name: "熔岩爆裂", value: "poe2_skill:Molten_Blast" },
    { name: "龙卷射击", value: "poe2_skill:Tornado_Shot" },
    { name: "纠缠", value: "poe2_skill:Entangle" },
    { name: "鞭笞藤蔓", value: "poe2_skill:Thrashing_Vines" },
    { name: "灾厄行者", value: "poe2_skill:Walking_Calamity" },
    { name: "灰烬连射", value: "poe2_skill:Ember_Fusillade" },
    { name: "活动炸弹", value: "poe2_skill:Living_Bomb" },
    { name: "激变", value: "poe2_skill:Snap" },
    { name: "剧毒增生", value: "poe2_skill:Toxic_Growth" },
    { name: "犀甲陆象", value: "poe2_skill:Elephant_Tortoise" },
    { name: "月神赐福", value: "poe2_skill:Lunar_Blessing" },
    { name: "镇魂曲", value: "poe2_skill:Requiem" },
    { name: "电弧", value: "poe2_skill:Arc" },
    { name: "狼群", value: "poe2_skill:Wolf_Pack" },
    { name: "冰川之刺", value: "poe2_skill:Glacial_Cascade" },
    { name: "德瑞的毁灭", value: "poe2_skill:Doedres_Undoing" },
    { name: "支援火力", value: "poe2_skill:Supporting_Fire" },
    { name: "冰尖箭矢", value: "poe2_skill:Ice-Tipped_Arrows" },
    { name: "烈性转化", value: "poe2_skill:Explosive_Transmutation" },
    { name: "魔力耀斑", value: "poe2_skill:Mana_Flare" },
    { name: "海幽夏的诛罚", value: "poe2_skill:Hayoxis_Fulmination" },
    { name: "护甲爆破", value: "poe2_skill:Armour_Explosion" },
    { name: "烈焰爆破", value: "poe2_skill:Flameblast" },
    { name: "虚空钟灵", value: "poe2_skill:Hollow_Focus" },
    { name: "驳斥（反制）", value: "poe2_skill:Refutation" },
    { name: "折射之镜", value: "poe2_skill:MirrorOfRefraction" },
    { name: "消融漩涡（自伤蓝药）", value: "poe2_skill:MeltingMaelstrom" },
    { name: "斥退", value: "poe2_skill:Repulsion" }
  ];

  const SPELL_NAME_MAP = new Map(POE2_SPELL_OPTIONS.map(opt => [opt.value, opt.name]));

  const REMOVE_DARK_LEVEL_NAMES = {
    "0.1": "0.1 极低曝光",
    "0.3": "0.3 偏暗柔和",
    "0.5": "0.5 适中曝光",
    "1.0": "1.0 默认推荐",
    "1.5": "1.5 高亮曝光",
    "990.5": "包含光影-增强1",
    "991": "包含光影-增强2",
    "991.5": "包含光影-增强3"
  };
  let selectedRemoveDarkLevel = localStorage.getItem("frei_remove_dark_level") || "1.0";

  // ── 小地图颜色设置状态与默认值 ──
  const DEFAULT_MINIMAP_COLOR = {
    exploredR: 0,
    exploredG: 0,
    exploredB: 0,
    exploredA: 76,
    borderR: 0,
    borderG: 120,
    borderB: 215,
    borderA: 26,
    exploringR: 0,
    exploringG: 128,
    exploringB: 255,
    exploringA: 128,
    borderGlow: 13,
    exploringGlow: 1,
    visibilityThreshold: 0.18
  };
  let currentMinimapColor = (() => {
    try {
      const saved = localStorage.getItem("frei_minimap_color");
      if (saved) return Object.assign({}, DEFAULT_MINIMAP_COLOR, JSON.parse(saved));
    } catch (_) {}
    return Object.assign({}, DEFAULT_MINIMAP_COLOR);
  })();

  function formatMinimapColorRule(c) {
    return `minimap_color:${c.exploredR}_${c.exploredG}_${c.exploredB}_${c.exploredA}_${c.borderR}_${c.borderG}_${c.borderB}_${c.borderA}_${c.exploringR}_${c.exploringG}_${c.exploringB}_${c.exploringA}_${c.borderGlow}_${c.exploringGlow}_${c.visibilityThreshold}`;
  }

  // ── 泥人 (MudMan) 状态与操作 ──
  const MUDMAN_TIER_NAMES = {
    0: "低级（技能除外）",
    1: "中级（包括技能）",
    2: "高级（包括模型）",
    3: "低级 + 大幅压缩材质",
    4: "中级 + 大幅压缩材质",
    5: "高级 + 大幅压缩材质"
  };

  // =========================================================================
  // PoE 2 商城外观 (MTX) 特效替换模块状态与方法
  // =========================================================================
  let allMtxEntries = [];
  let mtxCurrentTab = "all";
  let mtxSearchQuery = "";
  let selectedMtxNames = new Set();
  let selectedArmourSet = null;
  let selectedPortal = null;
  let selectedPet = null;
  let selectedMtxClass = "all";
  let mtxState = null;
  let mtxDbRequested = false;

  const MTX_CATEGORY_ICONS = {
    ArmourSets: "🛡️",
    Skills: "🔮",
    Portals: "🌀",
    Pets: "🐾",
    CharacterEffects: "🪽",
    Misc: "✨"
  };

  function fetchMtxDatabase() {
    if (mtxDbRequested && allMtxEntries.length > 0) return;
    mtxDbRequested = true;
    post("getMtxDatabase", {});
  }

  function getMtxEntryName(entry) {
    if (!entry) return "未知外观";
    if (entry.name && String(entry.name).trim()) return String(entry.name).trim();
    if (entry.Name && String(entry.Name).trim()) return String(entry.Name).trim();
    if (entry.displayName && String(entry.displayName).trim()) return String(entry.displayName).trim();
    if (entry.DisplayName && String(entry.DisplayName).trim()) return String(entry.DisplayName).trim();
    return "未知外观";
  }

  function getMtxDisplayNameZh(entry) {
    if (!entry) return "";
    if (entry.displayNameZh && String(entry.displayNameZh).trim()) return String(entry.displayNameZh).trim();
    if (entry.DisplayNameZh && String(entry.DisplayNameZh).trim()) return String(entry.DisplayNameZh).trim();
    return "";
  }

  function filterMtxEntries() {
    return allMtxEntries.filter(entry => {
      const name = getMtxEntryName(entry);
      const zh = getMtxDisplayNameZh(entry);
      const cat = entry.category || entry.Category || "Misc";
      const group = entry.group || entry.Group || "";
      const disp = entry.displayGroup || entry.DisplayGroup || "";
      const sub = entry.subtitle || entry.Subtitle || "";
      const isArmourGlobal = !!(entry.isArmourSetGlobal || entry.IsArmourSetGlobal);

      if (mtxCurrentTab !== "all") {
        if (mtxCurrentTab === "ArmourSets") {
          if (!isArmourGlobal && cat !== "ArmourSets") return false;
        } else {
          if (cat !== mtxCurrentTab) return false;
        }
      }
      if (mtxSearchQuery) {
        const q = mtxSearchQuery.toLowerCase();
        const matchZh = zh && zh.toLowerCase().includes(q);
        const matchName = name && name.toLowerCase().includes(q);
        const matchGroup = group && group.toLowerCase().includes(q);
        const matchDisp = disp && disp.toLowerCase().includes(q);
        const matchSub = sub && sub.toLowerCase().includes(q);
        if (!matchZh && !matchName && !matchGroup && !matchDisp && !matchSub) return false;
      }
      return true;
    });
  }

  function renderMtxGrid() {
    const grid = element("mtxGrid");
    if (!grid) return;

    if (allMtxEntries.length === 0) {
      grid.innerHTML = `<div style="grid-column: 1 / -1; padding: 40px; text-align: center; color: var(--muted, #94a3b8); font-size: 13px;">
        👑 正在从内嵌资源库加载 PoE 2 商城外观特效数据库 (843 项)...
      </div>`;
      return;
    }

    const filtered = filterMtxEntries();
    if (filtered.length === 0) {
      grid.innerHTML = `<div style="grid-column: 1 / -1; padding: 40px; text-align: center; color: var(--muted, #94a3b8); font-size: 13px;">
        没有找到匹配 "${escapeHtml(mtxSearchQuery)}" 的外观特效。
      </div>`;
      return;
    }

    const cardsHtml = filtered.map(entry => {
      const name = getMtxEntryName(entry);
      const zh = getMtxDisplayNameZh(entry);
      const mainTitle = zh || name;
      const cat = entry.category || entry.Category || "Misc";
      const group = entry.group || entry.Group || "";
      const disp = entry.displayGroup || entry.DisplayGroup || "";
      const sub = entry.subtitle || entry.Subtitle || group || "";
      const tag = disp || cat;
      const isArmour = (entry.isArmourSetGlobal || entry.IsArmourSetGlobal) || cat === "ArmourSets";
      const isPortal = cat === "Portals";
      const isPet = cat === "Pets";

      let isSelected = false;
      let inputType = "checkbox";

      if (isArmour) {
        isSelected = (selectedArmourSet === name);
        inputType = "radio";
      } else if (isPortal) {
        isSelected = (selectedPortal === name);
        inputType = "radio";
      } else if (isPet) {
        isSelected = (selectedPet === name);
        inputType = "radio";
      } else {
        isSelected = selectedMtxNames.has(name);
        inputType = "checkbox";
      }

      const iconEmoji = MTX_CATEGORY_ICONS[cat] || "👑";
      const selectedClass = isSelected ? " selected" : "";
      const radioName = isArmour ? "mtxRadioArmour" : (isPortal ? "mtxRadioPortal" : (isPet ? "mtxRadioPet" : ""));

      return `
        <div class="mtx-card${selectedClass}" data-name="${escapeHtml(name)}" data-category="${cat}">
          <div class="mtx-card-thumb">${iconEmoji}</div>
          <div class="mtx-card-info">
            <div class="mtx-card-title" title="${escapeHtml(mainTitle)} (${escapeHtml(name)})">${escapeHtml(mainTitle)}</div>
            <div class="mtx-card-sub" title="${escapeHtml(name)} · ${escapeHtml(sub)}">
              <span>${escapeHtml(name)}</span>
              ${sub && sub !== name ? `<span class="mtx-card-tag">${escapeHtml(sub)}</span>` : ""}
            </div>
          </div>
          <div class="mtx-card-check">
            <input type="${inputType}" ${radioName ? `name="${radioName}"` : ""} ${isSelected ? "checked" : ""} tabindex="-1">
          </div>
        </div>
      `;
    }).join("");

    grid.innerHTML = cardsHtml;

    grid.querySelectorAll(".mtx-card").forEach(card => {
      card.addEventListener("click", () => {
        const name = card.dataset.name;
        const entry = allMtxEntries.find(e => getMtxEntryName(e) === name);
        if (!entry) return;

        const entryName = getMtxEntryName(entry);
        const cat = entry.category || entry.Category;
        const isArmour = (entry.isArmourSetGlobal || entry.IsArmourSetGlobal) || cat === "ArmourSets";
        const isPortal = cat === "Portals";
        const isPet = cat === "Pets";

        if (isArmour) {
          selectedArmourSet = (selectedArmourSet === entryName) ? null : entryName;
        } else if (isPortal) {
          selectedPortal = (selectedPortal === entryName) ? null : entryName;
        } else if (isPet) {
          selectedPet = (selectedPet === entryName) ? null : entryName;
        } else {
          if (selectedMtxNames.has(entryName)) {
            selectedMtxNames.delete(entryName);
          } else {
            selectedMtxNames.add(entryName);
          }
        }

        renderMtxSummary();
        renderMtxGrid();
      });
    });
  }

  function renderMtxSummary() {
    const lblArmour = element("lblSelectedArmour");
    const lblPortal = element("lblSelectedPortal");
    const lblPet = element("lblSelectedPet");
    const lblOther = element("lblSelectedOtherCount");

    function formatSummaryName(enName) {
      if (!enName) return "(未选择)";
      const found = allMtxEntries.find(e => getMtxEntryName(e) === enName);
      if (found) {
        const zh = getMtxDisplayNameZh(found);
        if (zh) return `${zh} (${enName})`;
      }
      return enName;
    }

    if (lblArmour) lblArmour.innerText = formatSummaryName(selectedArmourSet);
    if (lblPortal) lblPortal.innerText = formatSummaryName(selectedPortal);
    if (lblPet) lblPet.innerText = formatSummaryName(selectedPet);
    if (lblOther) lblOther.innerText = `${selectedMtxNames.size} 款`;
  }

  function renderMtxUi() {
    renderMtxSummary();
    renderMtxGrid();

    const badgeStatus = element("badgeMtxStatus");
    const badgeCount = element("badgeMtxCount");
    const hubStatus = element("hubStatus-mtxpatch");

    const applied = mtxState ? mtxState.applied : false;
    const files = mtxState ? mtxState.patchedFiles : 0;
    const total = allMtxEntries.length > 0 ? allMtxEntries.length : (mtxState?.totalCount || 843);

    if (badgeCount) badgeCount.innerText = `(共 ${total} 款特效)`;

    if (badgeStatus) {
      if (applied) {
        badgeStatus.innerText = `已应用 (${files} 个文件)`;
        badgeStatus.style.background = "rgba(52, 211, 153, 0.15)";
        badgeStatus.style.borderColor = "rgba(52, 211, 153, 0.4)";
        badgeStatus.style.color = "#34d399";
      } else {
        badgeStatus.innerText = "未应用";
        badgeStatus.style.background = "rgba(245, 158, 11, 0.15)";
        badgeStatus.style.borderColor = "rgba(245, 158, 11, 0.3)";
        badgeStatus.style.color = "#fbbf24";
      }
    }

    if (hubStatus) {
      if (applied) {
        hubStatus.textContent = `● 已应用 (${files} 个文件)`;
        hubStatus.style.color = "#34d399";
      } else {
        hubStatus.textContent = "● 未应用";
        hubStatus.style.color = "var(--text-muted, #94a3b8)";
      }
    }
  }

  function applyMtxPatchAction() {
    const selClass = element("selMtxCharacterClass");
    const clsCode = selClass ? selClass.value : "all";

    // 与泥人互斥：商城外观整文件替换 .ao，会抹掉泥人对模型做的特效清理。
    if (mudManState && mudManState.applied) {
      if (!confirm('检测到泥人补丁已应用。\n\n商城外观与泥人会互相覆盖（两者都改角色模型 .ao 文件）：\n· 现在应用外观，泥人对模型的特效清理会失效\n· 之后重新应用泥人，外观又会看不见\n\n确定继续吗？')) return;
    }

    showToast("正在应用外观商城特效替换...");
    showEffectOperationOverlay("正在应用 PoE 2 外观商城特效替换，写入期间请勿关闭窗口...", 90);

    post("applyMtxPatch", {
      selectedNames: Array.from(selectedMtxNames),
      selectedArmourSetName: selectedArmourSet,
      selectedPortalName: selectedPortal,
      selectedPetName: selectedPet,
      selectedClassCode: clsCode
    });
  }

  function restoreMtxPatchAction() {
    if (!confirm("确定要还原所有商城外观特效吗？这将恢复客户端被修改的全部外观文件至官方纯净原版。")) return;

    selectedArmourSet = null;
    selectedPortal = null;
    selectedPet = null;
    selectedMtxNames.clear();
    renderMtxUi();

    showToast("正在还原外观特效至纯净原版...");
    showEffectOperationOverlay("正在还原外观特效至纯净原版...", 90);

    post("restoreMtxPatch", {});
  }

  let selectedMudManTier = -1;
  let healthBarState = null;
  let mudManState = null;

  function renderHealthBarUi() {
    const chk = element('chkOpt-healthbar');
    const badge = element('badgeHealthBarSummary');
    const applied = healthBarState ? healthBarState.applied : false;
    const files = healthBarState ? healthBarState.patchedFiles : 0;

    if (chk) chk.checked = applied;
    if (badge) {
      badge.innerHTML = applied
        ? '<span style="color: #34d399;">(已开启 · ' + files + ' 个文件)</span>'
        : '(未开启)';
    }
  }

  function toggleHealthBar(enabled) {
    if (enabled) {
      showToast('正在应用头顶血条补丁...');
      showEffectOperationOverlay('正在应用头顶血条补丁，写入期间请勿关闭窗口...', 90);
      post('applyHealthBar', {});
    } else {
      showToast('正在还原头顶血条补丁...');
      showEffectOperationOverlay('正在还原头顶血条补丁...', 90);
      post('restoreHealthBar', {});
    }
  }
  function renderMudManUi() {
    const radios = document.querySelectorAll('.mudman-tier-radio');
    radios.forEach(radio => {
      const tier = parseInt(radio.value, 10);
      radio.checked = (tier === selectedMudManTier);
    });

    const chkMud = element('chkOpt-mudman');
    if (chkMud) {
      chkMud.checked = (selectedMudManTier >= 0);
    }

    const badgeSummary = element('badgeMudManSummary');
    if (badgeSummary) {
      if (selectedMudManTier >= 0) {
        badgeSummary.innerText = `(当前: ${MUDMAN_TIER_NAMES[selectedMudManTier] || '已开启'})`;
        badgeSummary.style.color = '#fb923c';
      } else {
        badgeSummary.innerText = '(未开启)';
        badgeSummary.style.color = 'var(--muted)';
      }
    }

    const statusEl = element('mudManTableStatus');
    if (statusEl) {
      if (!mudManState) {
        statusEl.textContent = '';
      } else if (!mudManState.available) {
        statusEl.innerHTML = `<span style="color: #f87171;">⚠️ 贴图表不可用：${mudManState.error || '未知原因'}</span>`;
      } else {
        const applied = mudManState.applied
          ? `<span style="color: #34d399;">已应用 ${mudManState.patchedFiles} 个文件</span>`
          : '<span style="color: var(--muted);">未应用</span>';

        // 还原点：应用前自动建立、同版本复用。版本不符时必须显式警告，
        // 否则用户点还原会回滚到旧索引，客户端起不来。
        const rp = mudManState.restorePoint;
        let restoreInfo;
        if (rp && rp.exists) {
          restoreInfo = rp.matchesCurrent
            ? ` · <span style="color: #34d399;">还原点 ${rp.gameVersion}</span>`
            : ` · <span style="color: #f87171;">⚠️ 还原点 ${rp.gameVersion} 与当前版本不符，还原已禁用</span>`;
        } else {
          restoreInfo = ' · <span style="color: var(--muted);">还原点将在首次应用时建立</span>';
        }

        statusEl.innerHTML =
          `贴图表已就绪：名单 ${mudManState.listCount.toLocaleString()} 条 + 定制 ${mudManState.customCount.toLocaleString()} 条 · ${applied}${restoreInfo}`;
      }
    }

    // 泥人没有独立工作台，状态回显到卡片标题旁的 badge
    const hubStatus = element('badgeMudManSummary');
    if (hubStatus) {
      if (mudManState?.applied) {
        hubStatus.textContent = `● 已应用（${mudManState.patchedFiles} 个文件）`;
        hubStatus.style.color = '#fb923c';
      } else {
        hubStatus.textContent = '(未开启)';
        hubStatus.style.color = 'var(--muted)';
      }
    }
  }

  function restoreMudMan() {
    const rp = mudManState?.restorePoint;
    if (rp && rp.exists && !rp.matchesCurrent) {
      showToast(`还原点是 ${rp.gameVersion} 版本创建的，与当前客户端不符，已禁止还原`);
      return;
    }
    if (!confirm('确定要还原泥人改动的全部文件吗？将回到首次应用泥人之前的干净状态。')) return;
    selectedMudManTier = -1;
    renderMudManUi();
    showToast('正在还原泥人改动...');
    showEffectOperationOverlay('正在还原泥人改动...', 90);
    post('restoreMudMan', {});
  }

  function renderEffectPatchUi() {
    // 0. 同步去黑暗档位状态
    const darkRule = Array.from(selectedEffectRules).find(x => x.startsWith("remove_dark:"));
    if (darkRule) {
      const parsedLvl = darkRule.split(":")[1];
      if (parsedLvl && REMOVE_DARK_LEVEL_NAMES[parsedLvl]) {
        selectedRemoveDarkLevel = parsedLvl;
        localStorage.setItem("frei_remove_dark_level", selectedRemoveDarkLevel);
      }
    }

    const hasRemoveDark = Array.from(selectedEffectRules).some(x =>
      x === "remove_dark" || x.startsWith("remove_dark:") || x === "dark_light_objects");

    document.querySelectorAll(".remove-dark-level-radio").forEach(radio => {
      radio.checked = (radio.value === selectedRemoveDarkLevel);
    });

    const badgeDarkSummary = element("badgeRemoveDarkSummary");
    if (badgeDarkSummary) {
      if (hasRemoveDark) {
        badgeDarkSummary.innerText = `(当前: ${REMOVE_DARK_LEVEL_NAMES[selectedRemoveDarkLevel] || selectedRemoveDarkLevel})`;
        badgeDarkSummary.style.color = "#facc15";
      } else {
        badgeDarkSummary.innerText = "(未开启)";
        badgeDarkSummary.style.color = "var(--muted)";
      }
    }

    // 1. 同步画面与环境规则复选框
    document.querySelectorAll(".effect-rule-checkbox").forEach(chk => {
      const rules = chk.dataset.rule.split(",");
      chk.checked = rules.every(r => {
        const cleanR = r.trim();
        if (cleanR === "view_distance") {
          return Array.from(selectedEffectRules).some(x => x.startsWith("view_distance"));
        }
        if (cleanR === "remove_dark") {
          return hasRemoveDark;
        }
        if (cleanR === "minimap_visibility") {
          return Array.from(selectedEffectRules).some(x =>
            x === "minimap_visibility" || x.startsWith("minimap_color:") || x.startsWith("minimap_visibility:"));
        }
        if (chk.id === "chkOpt-fog_only" && selectedEffectRules.has("fog_env_objects")) {
          return true;
        }
        return selectedEffectRules.has(cleanR);
      });
    });

    // 2. 同步视距下拉倍率选择
    const selRatio = element("selViewDistanceRatio");
    if (selRatio) {
      const vdRule = Array.from(selectedEffectRules).find(x => x.startsWith("view_distance:"));
      if (vdRule) {
        selRatio.value = vdRule.split(":")[1];
      } else if (state?.settings?.viewDistanceRatio) {
        selRatio.value = Number(state.settings.viewDistanceRatio).toFixed(1);
      }
    }

    // 3. 同步音频复选框
    const chkAudio = element("chkSpellIncludeAudio");
    if (chkAudio) chkAudio.checked = spellIncludeAudio;

    // 4. 同步技能摘要与徽章状态
    updateSpellBlockBadgeAndSummary();

    // 若技能设置弹窗当前为显示状态，同步更新其内容
    const spellModal = element("spellBlockSettingsModal");
    if (spellModal && !spellModal.classList.contains("hidden")) {
      renderSpellBlockModal();
    }

    // 5. 同步泥人状态
    renderMudManUi();

    // 5.1 同步头顶血条状态
    renderHealthBarUi();

    // 6. 更新 Hub 卡片状态徽章
    const mudManActive = selectedMudManTier >= 0 ? 1 : 0;
    const healthBarActive = healthBarState && healthBarState.applied ? 1 : 0;
    const totalActive = selectedEffectRules.size + selectedBlockedSpells.size + mudManActive + healthBarActive;
    const hubStatus = element("hubStatus-effectpatch");
    if (hubStatus) {
      hubStatus.innerText = totalActive > 0 ? `● ${totalActive} 项已启用` : "● 0 项已启用";
      hubStatus.style.color = totalActive > 0 ? "var(--accent)" : "";
    }
  }

  function renderSpellBlockModal() {
    const container = element("spellGridContainer");
    if (!container) return;

    const kw = (spellSearchKeyword || "").trim().toLowerCase();
    const visibleSpells = POE2_SPELL_OPTIONS.filter(opt => {
      const isChecked = selectedBlockedSpells.has(opt.value);
      if (spellFilterOnlyChecked && !isChecked) return false;
      if (!kw) return true;
      return opt.name.toLowerCase().includes(kw) || opt.value.toLowerCase().includes(kw);
    });

    if (visibleSpells.length === 0) {
      container.innerHTML = `<div style="grid-column: 1 / -1; text-align: center; color: var(--muted); padding: 36px 10px; font-size: 12px;">未找到匹配的技能或装备特效</div>`;
    } else {
      container.innerHTML = visibleSpells.map(opt => {
        const isChecked = selectedBlockedSpells.has(opt.value);
        return `
          <label class="spell-grid-item ${isChecked ? "active" : ""}" title="${opt.name} (${opt.value})">
            <input type="checkbox" class="spell-checkbox" data-spell="${opt.value}" ${isChecked ? "checked" : ""}>
            <span class="spell-name">${opt.name}</span>
          </label>
        `;
      }).join("");

      container.querySelectorAll(".spell-checkbox").forEach(chk => {
        chk.addEventListener("change", () => {
          const val = chk.dataset.spell;
          if (chk.checked) {
            selectedBlockedSpells.add(val);
            chk.closest(".spell-grid-item")?.classList.add("active");
          } else {
            selectedBlockedSpells.delete(val);
            chk.closest(".spell-grid-item")?.classList.remove("active");
          }
          updateSpellBlockBadgeAndSummary();
        });
      });
    }

    updateSpellBlockBadgeAndSummary();
  }

  function updateSpellBlockBadgeAndSummary() {
    const count = selectedBlockedSpells.size;
    const countBadge = element("spellSelectCountBadge");
    if (countBadge) {
      countBadge.innerText = `已选择 ${count} 项`;
    }

    const filterBtn = element("btnSpellFilterOnlyChecked");
    if (filterBtn) {
      filterBtn.innerText = spellFilterOnlyChecked ? `全部显示 (${count})` : `仅看已选 (${count})`;
      if (spellFilterOnlyChecked) {
        filterBtn.style.color = "var(--accent)";
        filterBtn.style.borderColor = "var(--accent)";
      } else {
        filterBtn.style.color = "";
        filterBtn.style.borderColor = "";
      }
    }

    const chkMaster = element("chkOpt-spell_block_master");
    if (chkMaster) chkMaster.checked = count > 0;

    const badgeSummary = element("badgeSpellSummary");
    if (badgeSummary) {
      if (count > 0) {
        const sampleNames = Array.from(selectedBlockedSpells)
          .map(val => SPELL_NAME_MAP.get(val) || (val === "lightning_spark" ? "电球" : val))
          .slice(0, 3);
        const moreSuffix = count > 3 ? ` 等共 ${count} 项` : "";
        badgeSummary.innerText = `(已开启: ${sampleNames.join(", ")}${moreSuffix})`;
        badgeSummary.style.color = "var(--accent)";
      } else {
        badgeSummary.innerText = "(未开启)";
        badgeSummary.style.color = "var(--muted)";
      }
    }

    const mudManActive = selectedMudManTier >= 0 ? 1 : 0;
    const totalActive = selectedEffectRules.size + count + mudManActive;
    const hubStatus = element("hubStatus-effectpatch");
    if (hubStatus) {
      hubStatus.innerText = totalActive > 0 ? `● ${totalActive} 项已启用` : "● 0 项已启用";
      hubStatus.style.color = totalActive > 0 ? "var(--accent)" : "";
    }
  }

  function showEffectOperationOverlay(actionTitle, timeoutSeconds = 60) {
    let overlay = element("effectPatchOperationOverlay");
    if (!overlay) {
      overlay = document.createElement("div");
      overlay.id = "effectPatchOperationOverlay";
      overlay.className = "effect-patch-overlay";
      overlay.innerHTML = `
        <div class="effect-overlay-card">
          <div class="effect-overlay-spinner animate-spin">⟳</div>
          <div id="effectOverlayTitle" class="effect-overlay-title">正在重写游戏索引...</div>
          <div class="effect-overlay-desc">正在写入游戏资源并重新校验，预计耗时约 25~35 秒。<br><strong style="color: #f87171;">⚠️ 写入过程中请勿关闭窗口、勿启动游戏或拔掉硬盘！</strong></div>
          <div class="effect-overlay-progress-bar">
            <div id="effectOverlayProgressFill" class="effect-overlay-progress-fill"></div>
          </div>
          <div id="effectOverlayTime" class="effect-overlay-time">预计剩余时间: 30 秒</div>
        </div>
      `;
      document.body.appendChild(overlay);
    }

    const titleEl = element("effectOverlayTitle");
    if (titleEl) titleEl.innerText = actionTitle || "正在应用特效优化...";
    overlay.classList.remove("hidden");
    overlay.style.display = "flex";

    let remaining = 30;
    let elapsed = 0;
    const fill = element("effectOverlayProgressFill");
    const timeText = element("effectOverlayTime");
    if (fill) fill.style.width = "0%";

    if (window._effectOverlayTimer) clearInterval(window._effectOverlayTimer);
    window._effectOverlayTimer = setInterval(() => {
      remaining--;
      elapsed++;
      if (remaining < 0) remaining = 0;
      if (timeText) {
        if (remaining > 0) {
          timeText.innerText = `预计剩余时间: 约 ${remaining} 秒`;
        } else if (elapsed < 60) {
          timeText.innerText = "正在完成最终校验与安全备份落盘...";
        } else {
          timeText.innerHTML = `已耗时超 60 秒，<a href="javascript:void(0)" onclick="window.patcherUi?.hideOverlay()" style="color:#38bdf8;text-decoration:underline;">点此解除锁定</a>`;
        }
      }
      if (fill) {
        const percent = Math.min(95, Math.round(((30 - remaining) / 30) * 100));
        fill.style.width = percent + "%";
      }
    }, 1000);
  }

  function hideEffectOperationOverlay() {
    if (window._effectOverlayTimer) {
      clearInterval(window._effectOverlayTimer);
      window._effectOverlayTimer = null;
    }
    const overlay = element("effectPatchOperationOverlay");
    if (overlay) {
      const fill = element("effectOverlayProgressFill");
      if (fill) fill.style.width = "100%";
      setTimeout(() => {
        overlay.classList.add("hidden");
        overlay.style.display = "none";
      }, 500);
    }
  }

  function getSelectedEffectRulesWithRatio() {
    const rules = [];
    document.querySelectorAll(".effect-rule-checkbox:checked").forEach(chk => {
      chk.dataset.rule.split(",").forEach(r => rules.push(r.trim()));
    });
    const uniqueRules = Array.from(new Set(rules));
    const ratioVal = parseFloat(element("selViewDistanceRatio")?.value || "2.0") || 2.0;
    const ratioStr = ratioVal.toFixed(1);

    const finalRules = uniqueRules.map(r => {
      if (r === "view_distance") return `view_distance:${ratioStr}`;
      if (r === "remove_dark") return `remove_dark:${selectedRemoveDarkLevel}`;
      if (r === "minimap_visibility" || r.startsWith("minimap_color:") || r.startsWith("minimap_visibility:")) {
        return formatMinimapColorRule(currentMinimapColor);
      }
      return r;
    });
    return { rules: finalRules, ratio: ratioVal };
  }

  function applySpellBlocks() {
    const spells = Array.from(selectedBlockedSpells);
    const includeAudio = element("chkSpellIncludeAudio")?.checked ?? true;
    showToast("正在应用技能与装备特效屏蔽...");
    showEffectOperationOverlay("正在应用技能与装备特效屏蔽...", 60);
    post("applySpellBlock", { spellIds: spells, includeAudio });
  }

  function restoreSpellBlocks() {
    const configured = state?.settings?.blockedSpellIds || [];
    const spells = configured.length > 0 ? Array.from(configured) : Array.from(selectedBlockedSpells);
    if (spells.length === 0) {
      showToast("当前没有已屏蔽的技能需要还原");
      return;
    }
    if (!confirm(`确定要还原这 ${spells.length} 个技能的特效吗？`)) return;
    showToast("正在还原技能特效...");
    showEffectOperationOverlay("正在还原技能特效...", 60);
    post("restoreSpellBlock", { spellIds: spells });
  }

  function restoreAllEffectSettings() {
    if (!confirm("确定要还原所有特效与画面优化吗？此操作将写回官方原版资源。")) return;
    showToast("正在还原所有特效与性能优化...");
    showEffectOperationOverlay("正在还原所有特效与性能优化...", 60);
    selectedMudManTier = -1;
    renderMudManUi();
    post("applyAllEffects", {
      ruleIds: [],
      spellIds: [],
      includeAudio: true,
      mudManTier: -1
    });
  }

  function applyAllEffectSettings() {
    const { rules, ratio } = getSelectedEffectRulesWithRatio();
    const masterChecked = element("chkOpt-spell_block_master")?.checked ?? (selectedBlockedSpells.size > 0);
    const spells = masterChecked ? Array.from(selectedBlockedSpells) : [];
    const includeAudio = element("chkSpellIncludeAudio")?.checked ?? true;
    const mudManChecked = element("chkOpt-mudman")?.checked ?? (selectedMudManTier >= 0);
    const mudManTier = mudManChecked ? (selectedMudManTier >= 0 ? selectedMudManTier : 4) : -1;

    // 这是泥人唯一真实的应用入口，互斥与可用性校验必须放在这里。
    if (mudManTier >= 3 && mudManState && !mudManState.available) {
      showToast("贴图表不可用，无法应用含贴图压缩的档位");
      return;
    }

    // 泥人与商城外观改的是同一批角色模型 .ao：泥人清空特效块，外观整文件替换。
    // 两者互相覆盖，必须先告知用户，不能静默让一方失效。
    if (mudManTier >= 0 && mtxState && mtxState.applied) {
      if (!confirm("检测到商城外观补丁已应用。\n\n泥人与商城外观会互相覆盖（两者都改角色模型 .ao 文件）：\n· 现在应用泥人，商城外观会失效\n· 之后重新应用外观，泥人又会被覆盖\n\n确定继续吗？")) return;
    }

    showToast("正在保存并应用特效设置...");
    showEffectOperationOverlay("正在保存并应用特效设置...", 60);

    post("applyAllEffects", {
      ruleIds: rules,
      spellIds: spells,
      includeAudio: includeAudio,
      viewDistanceRatio: ratio,
      mudManTier: mudManTier
    });
  }

  // 模态框联想与更新
  function onModalInput(text) {
    updateModalBox();
    const dropdown = element("autocompleteDropdown");
    if (!dropdown) return;
    const q = text.trim().toLowerCase();
    if (!q) {
      dropdown.classList.add("hidden");
      return;
    }

    // 词库条目来自客户端 CSD 扫描结果，字段是 displayName 而非 text。
    const statText = m => m.displayName || m.text || m.statId || "";
    const matches = CLIENT_STAT_DICTIONARY
      .filter(item => statText(item).toLowerCase().includes(q))
      .slice(0, 8);
    if (matches.length === 0) {
      dropdown.classList.add("hidden");
      return;
    }

    dropdown.innerHTML = "";
    matches.forEach(m => {
      const opt = document.createElement("div");
      opt.className = "autocomplete-item";
      const label = statText(m);
      opt.innerHTML = `
        <div style="font-size: 11px; color: #fff;">${escapeHtml(label)}</div>
        <div style="display: flex; gap: 6px; font-size: 10px; color: var(--text-muted, #94a3b8); margin-top: 2px;">
          <span>分组: ${escapeHtml(m.group || "")}</span>
          ${m.tag ? `<span style="color: ${escapeHtml(m.color || '#d4a754')}">#${escapeHtml(m.tag)}</span>` : ''}
        </div>
      `;
      opt.addEventListener("click", () => {
        element("modalRuleText").value = label;
        if (m.group && element("modalRuleGroup")) element("modalRuleGroup").value = m.group;
        if (m.color && element("modalRuleColor")) element("modalRuleColor").value = m.color;
        if (m.style && element("modalRuleStyle")) element("modalRuleStyle").value = m.style;
        if (m.tag && element("modalRuleTag")) element("modalRuleTag").value = m.tag;
        dropdown.classList.add("hidden");
        updateModalBox();
      });
      dropdown.appendChild(opt);
    });
    dropdown.classList.remove("hidden");
  }

  function updateModalBox() {
    const text = element("modalRuleText")?.value.trim() || "示例：玩家受到 (15-20)% 的物理伤害反射";
    const style = element("modalRuleStyle")?.value || "Whole";
    const color = element("modalRuleColor")?.value || "#e64545";
    const box = element("modalPreviewBox");

    // 色值旁的十六进制文本必须跟随取色器，否则用户改色后那个数字恒定不变。
    const hexLabel = element("modalColorHex");
    if (hexLabel) hexLabel.textContent = color;

    if (!box) return;

    // 文本来自用户输入，颜色来自取色器，两者都要经转义再拼进 HTML。
    const safeText = escapeHtml(text);
    const safeColor = escapeHtml(color);

    if (style === "NumberOnly") {
      const rendered = safeText.replace(/((\+|-)?#%?|(\+|-)?\d+(?:\.\d+)?%?|\(\d+[-~]\d+\)%?|\(#[-~]#\)%?|\{\d+\}%?)/g, `<span style="color: ${safeColor}; font-weight: bold;">$1</span>`);
      box.innerHTML = `预览：${rendered}`;
    } else {
      box.innerHTML = `预览：<span style="color: ${safeColor}; font-weight: 500;">${safeText}</span>`;
    }
  }

  function fillModal(text, group, style, color, tag) {
    if (element("modalRuleText")) element("modalRuleText").value = text;
    if (element("modalRuleGroup")) element("modalRuleGroup").value = group;
    if (element("modalRuleStyle")) element("modalRuleStyle").value = style;
    if (element("modalRuleColor")) element("modalRuleColor").value = color;
    if (element("modalRuleTag")) element("modalRuleTag").value = tag;
    updateModalBox();
  }

  function formatSize(bytes) {
    // 体积缺失时返回 "-"，不能编造一个具体数字：快照卡片上写「115.4 MB」
    // 会让用户以为文件真的存在且大小正常。
    if (!bytes || bytes <= 0) return "-";
    return (bytes / 1024 / 1024).toFixed(1) + " MB";
  }

  /// 图标体积在 KB 量级，复用 MB 版会全部显示成 0.0 MB。
  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "-";
    if (bytes < 1024) return bytes + " B";
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + " KB";
    return (bytes / 1024 / 1024).toFixed(1) + " MB";
  }

  function formatDate(isoStr) {
    if (!isoStr) return "";
    try {
      const d = new Date(isoStr);
      return `${d.getMonth() + 1}-${d.getDate()} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    } catch {
      return isoStr;
    }
  }

  function renderSnapshots(snapshots) {
    const container = element("snapshotListContainer");
    if (!container) return;

    if (!Array.isArray(snapshots) || snapshots.length === 0) {
      container.innerHTML = `
        <div class="snapshot-empty-hint">
          暂无快照数据。首次应用补丁前将自动建立官方出厂纯净底包。
        </div>
      `;
      return;
    }

    const getSnapType = s => {
      const t = s.type !== undefined ? s.type : s.Type;
      if (t === 0 || t === "Official" || t === "official") return 0;
      if (t === 1 || t === "Manual" || t === "manual") return 1;
      if (t === 2 || t === "Auto" || t === "auto") return 2;
      return -1;
    };

    const isOfficial = s => getSnapType(s) === 0 || s.id === "clean_official" || s.Id === "clean_official";
    const isManual = s => getSnapType(s) === 1 && !isOfficial(s);
    const isAuto = s => getSnapType(s) === 2 && !isOfficial(s);

    const officialList = snapshots.filter(isOfficial);
    const manualList = snapshots.filter(isManual);
    const autoList = snapshots.filter(isAuto);

    let html = "";

    // 1. 官方出厂纯净底包
    html += `
      <div class="snapshot-group">
        <div class="snapshot-group-header">
          <span>🌟 官方出厂纯净底包 (出厂固化 · 永久锁定)</span>
        </div>
    `;
    if (officialList.length === 0) {
      html += `<div class="snapshot-empty-hint">官方底包将在首次写入补丁前自动抓取并固化。</div>`;
    } else {
      officialList.forEach(s => {
        const id = s.id || s.Id;
        const name = s.name || s.Name || "官方纯净原版备份";
        const desc = s.description || s.Description || "客户端原始未修改的纯净索引，随时可一键完全恢复出厂状态。";
        const size = s.sizeBytes !== undefined ? s.sizeBytes : s.SizeBytes;
        const created = s.createdAt || s.CreatedAt;
        const stale = Boolean(s.isStale);
        const version = s.gameVersion || "";
        html += `
          <div class="snapshot-card official${stale ? " stale" : ""}">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${escapeHtml(name)}</span>
                <span class="snapshot-tag official">官方出厂底包</span>
                ${stale ? '<span class="snapshot-tag stale">⚠ 版本不匹配</span>' : ""}
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 拍摄于: ${formatDate(created)}</span>
                ${version ? `<span>🎮 游戏版本: ${version}</span>` : ""}
                <span>📌 锁定状态: 禁止删除/覆盖</span>
              </div>
              <div style="font-size: 11px; color: var(--text-muted); margin-top: 2px;">
                ${stale
                  ? "该底包来自旧版本客户端，恢复会让客户端与资源不匹配而无法启动。下次启动会自动重建。"
                  : desc}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${escapeHtml(name)}"
                      ${stale ? "disabled" : ""}
                      title="${stale ? "版本不匹配，已禁用恢复" : "将客户端恢复为此版本，所有补丁重置为关闭状态"}">
                ↺ 恢复出厂纯净
              </button>
            </div>
          </div>
        `;
      });
    }
    html += `</div>`;

    // 2. 手动备份版本
    html += `
      <div class="snapshot-group">
        <div class="snapshot-group-header">
          <span>📌 手动备份版本 (用户命名 · 永久保留)</span>
          <span style="font-size: 10px; color: var(--text-muted);">共 ${manualList.length} 个版本</span>
        </div>
    `;
    if (manualList.length === 0) {
      html += `<div class="snapshot-empty-hint">暂无手动备份版本。可点击上方【💾 备份当前版本】创建。</div>`;
    } else {
      manualList.forEach(s => {
        const id = s.id || s.Id;
        const name = s.name || s.Name;
        const desc = s.description || s.Description || "用户手动创建的版本快照";
        const size = s.sizeBytes !== undefined ? s.sizeBytes : s.SizeBytes;
        const created = s.createdAt || s.CreatedAt;
        const modules = s.enabledModules || s.EnabledModules || [];
        const modulesHtml = Array.isArray(modules) && modules.length > 0
          ? modules.map(m => `<span class="snapshot-module-pill">${m}</span>`).join("")
          : `<span class="snapshot-module-pill">纯净状态</span>`;
        const stale = Boolean(s.isStale);
        const version = s.gameVersion || "";

        html += `
          <div class="snapshot-card${stale ? " stale" : ""}">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${escapeHtml(name)}</span>
                <span class="snapshot-tag manual">手动备份</span>
                ${stale ? '<span class="snapshot-tag stale">⚠ 版本不匹配</span>' : ""}
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 创建时间: ${formatDate(created)}</span>
                ${version ? `<span>🎮 ${version}</span>` : ""}
                <span>${stale ? "旧版本快照，48 小时后自动清理" : desc}</span>
              </div>
              <div class="snapshot-modules-row">
                ${modulesHtml}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${escapeHtml(name)}"
                      ${stale ? "disabled" : ""}
                      title="${stale ? "版本不匹配，已禁用恢复" : "恢复此版本"}">
                ↺ 恢复此版本
              </button>
              <button class="btn-delete-snap" data-snap-id="${id}" data-snap-name="${escapeHtml(name)}">
                🗑️ 删除
              </button>
            </div>
          </div>
        `;
      });
    }
    html += `</div>`;

    // 3. 自动备份历史
    html += `
      <div class="snapshot-group">
        <div class="snapshot-group-header">
          <span>🕒 自动备份历史 (应用补丁前自动记录 · 严格保留最近 5 版轮转)</span>
          <span style="font-size: 10px; color: var(--text-muted);">${autoList.length}/5 版</span>
        </div>
    `;
    if (autoList.length === 0) {
      html += `<div class="snapshot-empty-hint">暂无自动快照。每次在工作台应用或更新补丁时将自动拍摄。</div>`;
    } else {
      autoList.forEach(s => {
        const id = s.id || s.Id;
        const name = s.name || s.Name;
        const desc = s.description || s.Description || "打补丁前系统自动创建的备份";
        const size = s.sizeBytes !== undefined ? s.sizeBytes : s.SizeBytes;
        const created = s.createdAt || s.CreatedAt;
        const modules = s.enabledModules || s.EnabledModules || [];
        const modulesHtml = Array.isArray(modules) && modules.length > 0
          ? modules.map(m => `<span class="snapshot-module-pill">${m}</span>`).join("")
          : `<span class="snapshot-module-pill">默认/纯净</span>`;
        const stale = Boolean(s.isStale);
        const version = s.gameVersion || "";

        html += `
          <div class="snapshot-card${stale ? " stale" : ""}">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${escapeHtml(name)}</span>
                <span class="snapshot-tag auto">自动快照</span>
                ${stale ? '<span class="snapshot-tag stale">⚠ 版本不匹配</span>' : ""}
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 记录时间: ${formatDate(created)}</span>
                ${version ? `<span>🎮 ${version}</span>` : ""}
                <span>${stale ? "旧版本快照，48 小时后自动清理" : desc}</span>
              </div>
              <div class="snapshot-modules-row">
                ${modulesHtml}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${escapeHtml(name)}"
                      ${stale ? "disabled" : ""}
                      title="${stale ? "版本不匹配，已禁用恢复" : "恢复此版本"}">
                ↺ 恢复此版本
              </button>
              <button class="btn-delete-snap" data-snap-id="${id}" data-snap-name="${escapeHtml(name)}">
                🗑️ 删除
              </button>
            </div>
          </div>
        `;
      });
    }
    html += `</div>`;

    container.innerHTML = html;

    // 绑定恢复与删除事件
    container.querySelectorAll(".btn-restore-snap").forEach(btn => {
      btn.addEventListener("click", () => {
        const id = btn.dataset.snapId;
        const name = btn.dataset.snapName;
        if (confirm(`确定要恢复到版本快照「${name}」吗？\n\n系统将自动写回对应的客户端索引，并将软件设置与功能开关同步回滚为该快照当时的状态。`)) {
          showToast(`正在还原到快照：${name}...`);
          post("restorePatcherSnapshot", { snapshotId: id });
        }
      });
    });

    container.querySelectorAll(".btn-delete-snap").forEach(btn => {
      btn.addEventListener("click", () => {
        const id = btn.dataset.snapId;
        const name = btn.dataset.snapName;
        if (confirm(`确定要删除版本快照「${name}」吗？此操作不可逆。`)) {
          post("deletePatcherSnapshot", { snapshotId: id });
        }
      });
    });
  }

  function openCreateSnapshotModal() {
    const nameInp = element("inpSnapshotName");
    const descInp = element("inpSnapshotDesc");
    const err = element("createSnapshotError");
    if (err) err.innerText = "";
    const now = new Date();
    const timeStr = `${now.getMonth() + 1}-${now.getDate()} ${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
    if (nameInp) nameInp.value = `手动备份 (${timeStr})`;
    if (descInp) descInp.value = "";
    element("createSnapshotModal")?.classList.remove("hidden");
    nameInp?.focus();
    nameInp?.select();
  }

  // 渲染后端状态与回显
  function renderStatus() {
    if (!state) return;
    const dirInput = element('patcherGameDir');
    if (dirInput && document.activeElement !== dirInput) {
      dirInput.value = state.gameDir || '';
    }

    const badgeMode = element('patcherBadgeMode');
    if (badgeMode) {
      badgeMode.textContent = state.gameMode || '未知模式';
      badgeMode.className = 'patcher-badge ' + (state.gameMode === 'Bundles2' ? 'mode-bundles2' : 'mode-ggpk');
    }

    // 国服客户端原生即简体中文，汉化补丁没有意义，直接禁用整张卡片。
    applyTranslationAvailability(state);

    const badgeRun = element('patcherBadgeRun');
    if (badgeRun) {
      if (state.isGameRunning) {
        badgeRun.textContent = '游戏运行中 (请退出后打补丁)';
        badgeRun.className = 'patcher-badge running-warn';
      } else {
        badgeRun.textContent = '游戏未运行 (可安全操作)';
        badgeRun.className = 'patcher-badge running-safe';
      }
    }

    const badgeBackup = element('patcherBadgeBackup');
    if (badgeBackup) {
      badgeBackup.textContent = state.hasBackup ? '已具备纯净备份' : '尚未生成备份 (首次操作自动创建)';
      badgeBackup.className = 'patcher-badge ' + (state.hasBackup ? 'backup-ready' : '');
    }

    const feedbackBox = element('patcherFeedback');
    if (feedbackBox) {
      if (state.error) {
        feedbackBox.textContent = state.error;
        feedbackBox.className = 'patcher-feedback error';
      } else if (state.message) {
        feedbackBox.textContent = state.message;
        const isSuccess = state.message.includes('成功') || state.message.includes('完成');
        feedbackBox.className = 'patcher-feedback ' + (isSuccess ? 'success' : 'info');
      }
    }

    // 补丁任务执行期间禁用所有写入型按钮，避免并发改写同一批数据表。
    // 后端另有互斥闸兜底，这里只负责给用户即时反馈。
    applyBusyState(Boolean(state.patchBusy));
  }

  /**
   * 按补丁闸门状态启用/禁用写入型按钮。
   *
   * 后端在闸门释放后会单独下发 patcherBusyState，只带 patchBusy 一个字段：
   * 写入型 handler 的最后一条快照是在闸门仍被占用时发出的，若不再补一条，
   * 按钮会一直保持 disabled。
   */
  function applyBusyState(busy) {
    if (!busy) {
      hideEffectOperationOverlay();
    }
    for (const id of PATCH_ACTION_BUTTON_IDS) {
      const button = element(id);
      if (button) button.disabled = busy;
    }
    // 血条开关走 checkbox，不在按钮列表里，单独禁用
    const hbToggle = element('chkOpt-healthbar');
    if (hbToggle) hbToggle.disabled = busy;

    // 动态生成的写操作按钮（快照中心的恢复/删除、各类列表项按钮）
    // 无法预先登记进 PATCH_ACTION_BUTTON_IDS，用属性标记统一处理。
    // 之前漏掉这类按钮，用户可以在写盘期间并发点恢复或删除，导致状态错乱。
    document.querySelectorAll('[data-busy-guard]').forEach(node => {
      node.disabled = busy;
    });
  }

  /** 收到后端闸门状态同步消息。 */
  function receiveBusyState(payload) {
    if (!payload) return;
    applyBusyState(Boolean(payload.patchBusy));
  }

  // 会写客户端文件的静态按钮。只读扫描按钮（btnScan*）与预览按钮不在此列。
  //
  // 这里只管「页面里固定存在的按钮」。快照中心按条目动态生成的恢复/删除按钮
  // 无法预先登记，由 applyBusyState 统一扫描 data-busy-guard 属性处理。
  // 新增写操作按钮时：固定按钮加进本清单，动态按钮加 data-busy-guard="1"。
  const PATCH_ACTION_BUTTON_IDS = [
    'btnApplyTranslationPatch',
    'btnRestorePatches',
    'btnApplyCurrentWorkbench',
    'btnRestoreSnapshot',
    'btnConfirmCreateSnapshot',
    'btnTriggerCreateSnapInCenter',
    'btnApplyEffectRules',
    'btnRestoreEffectRules',
    'btnRestoreSpellBlocksInModal',
    'btnQuickApplyEffectPatch',
    'btnRestoreMudManInModal',
    'btnRestoreIconPatch',
    'btnApplyMtxPatch',
    'btnRestoreMtxPatch',
    'btnQuickApplyMtxPatch',
    'btnSaveFontPreset',
    'btnSaveRemoveDarkModal',
    'btnSaveSpellBlockModal',
      'btnSaveMudManModal',
      // 物价清洗也会改写客户端数据表，原先漏掉了
      'btnCleanPricePatch'
  ];

  // 同步 6 个模块卡片的真实状态。
  //
  // 这些 span 原本是硬编码文案（「● 14 条规则生效中」「● 5 种神图光柱激活」等），
  // 无论用户是否打过补丁都恒显示「生效中」，属虚假状态。
  // 现在按设置与实际数据实时计算：未启用时显示「○ 未启用」。
  function syncModuleHubStatus() {
    const set = (id, text, active) => {
      const el = element('hubStatus-' + id);
      if (!el) return;
      el.innerText = text;
      el.style.color = active ? '#34d399' : 'var(--text-muted, #94a3b8)';
    };

    const settings = state.settings || {};

    // 后端随快照下发的默认清单。用户尚未自定义时设置里的列表为 null，
    // 此时必须按默认项计数，否则会显示「● 0 条规则生效中」这种假 0。
    const defaultMapBeams = Array.isArray(state.defaultMapBeams) ? state.defaultMapBeams : [];

    // 词缀染色：显示实际启用的规则条数
    {
      const on = !!settings.colorModEnabled;
      const count = Array.isArray(settings.colorModRules)
        ? settings.colorModRules.filter(r => r && r.enabled).length
        : 0;
      set('colormod', on ? `● ${count} 条规则生效中` : '○ 未启用', on);
    }

    // 神图光柱：设置里没有自定义项时按后端默认清单计数
    {
      const on = !!settings.mapBeamEnabled;
      const targets = Array.isArray(settings.mapBeamTargets) && settings.mapBeamTargets.length > 0
        ? settings.mapBeamTargets
        : defaultMapBeams;
      const count = targets.filter(t => t && t.enabled).length;
      set('mapbeam', on ? `● ${count} 种神图光柱激活` : '○ 未启用', on);
    }

    // 宝箱增强
    {
      const on = !!settings.chestEnhanced;
      set('chests', on ? '● 陷阱防护开启' : '○ 未启用', on);
    }

    // 传言增强：设置里没有自定义项时按后端默认清单计数
    {
      const on = !!settings.rumourEnhanced;
      const items = Array.isArray(settings.rumourItems) && settings.rumourItems.length > 0
        ? settings.rumourItems
        : null;
      if (items) {
        const count = items.filter(i => i && i.enabled).length;
        set('rumours', on ? `● ${count} 项传言明细解析` : '○ 未启用', on);
      } else {
        // 未自定义时条目数等于后端默认字典大小；前端不重复持有该数据，
        // 只表述「默认明细」而不编造具体条数。
        set('rumours', on ? '● 默认传言明细解析' : '○ 未启用', on);
      }
    }

    // 字体：ClientPatchSettings 没有独立的「已写入」布尔字段，
    // 以字体族是否偏离系统默认作为生效依据。
    {
      const on = !!settings.fontFamily && settings.fontFamily !== 'Microsoft YaHei';
      set('fontmod', on ? '● 自定义字体已写入' : '○ 系统原生渲染', on);
    }

    // 物价：按启用状态
    {
      const on = !!settings.pricePatchEnabled;
      set('price', on ? '● 实时物价挂接中' : '○ 未启用', on);
    }
  }

  /** 1. 卡片点击进入工作台 */
  function bindHubCards() {
    document.querySelectorAll(".hub-card").forEach(card => {
      card.addEventListener("click", () => {
        const mod = card.dataset.module;
        if (mod) openWorkbench(mod);
      });
    });
  }

  /** 2. 卡片独立开关 */
  function bindModuleMasters() {
    Object.keys(moduleMasters).forEach(mod => {
      const toggle = element("masterToggle-" + mod);
      toggle?.addEventListener("click", e => e.stopPropagation());
      toggle?.addEventListener("change", e => toggleModuleMaster(mod, e.target.checked, e));
    });
  }

  /** 3. 工作台返回与直达下拉 */
  function bindWorkbenchNavigation() {
    element("btnBackToHub")?.addEventListener("click", backToHub);
    element("wbModuleSelect")?.addEventListener("change", e => openWorkbench(e.target.value));
  }

  /** 4. 路径更新 */
  function bindGameDirInput() {
    element('btnSetGameDir')?.addEventListener('click', () => {
      const path = element('patcherGameDir')?.value?.trim();
      post('setPatcherGameDir', { path });
    });
  }

  /** 5. 快照版本管理与还原纯净客户端 */
  function bindSnapshotControls() {
    element('btnBackupCurrentVersion')?.addEventListener('click', openCreateSnapshotModal);
    element('btnTriggerCreateSnapInCenter')?.addEventListener('click', openCreateSnapshotModal);
    element('btnCloseCreateSnapshotModal')?.addEventListener('click', () => element('createSnapshotModal')?.classList.add('hidden'));
    element('btnCancelCreateSnapshot')?.addEventListener('click', () => element('createSnapshotModal')?.classList.add('hidden'));
    element('btnConfirmCreateSnapshot')?.addEventListener('click', () => {
      const name = element('inpSnapshotName')?.value?.trim();
      const desc = element('inpSnapshotDesc')?.value?.trim() || '';
      if (!name) {
        if (element('createSnapshotError')) element('createSnapshotError').innerText = '快照名称不能为空';
        return;
      }
      showToast(`正在创建版本快照「${name}」...`);
      post('createPatcherSnapshot', { name, description: desc });
      element('createSnapshotModal')?.classList.add('hidden');
    });
    element('inpSnapshotName')?.addEventListener('keydown', e => {
      if (e.key === 'Enter') element('btnConfirmCreateSnapshot')?.click();
    });

    element('btnOpenSnapshotsModal')?.addEventListener('click', () => {
      element('snapshotCenterModal')?.classList.remove('hidden');
      post('getPatcherSnapshots');
    });
    element('btnCloseSnapshotCenterModal')?.addEventListener('click', () => {
      element('snapshotCenterModal')?.classList.add('hidden');
    });

    const handleRestore = () => {
      if (confirm('确定要恢复为出厂纯净客户端吗？此操作将秒级还原所有由 FreiAtlas 打上的补丁文件，并重置各补丁开关。')) {
        showToast('正在恢复纯净客户端...');
        post('restoreAllPatches');
      }
    };
    element('btnRestorePatches')?.addEventListener('click', handleRestore);

    // 官方纯净简中汉化一键应用
    element('btnApplyTranslationPatch')?.addEventListener('click', () => {
      // 国服原生简中，直接拦截（按钮已置灰，这里防御绕过 UI 的调用）
      if (state?.isChina) {
        showToast('国服客户端原生已是简体中文，无需应用汉化补丁');
        return;
      }
      if (confirm('确定要对当前客户端应用【官方纯净简中汉化】吗？\n\n• 全量改写 CSD 词缀与属性描述文件\n• 注入官方基础物品与暗金译名，并汉化核心数据表\n• 纯净解耦，不与字体、物价强行捆绑\n• 全程受快照保护，可随时一键恢复纯净底包')) {
        showToast('正在开始注入官方简中汉化补丁，请稍候...');
        post('applyTranslationPatch');
      }
    });
  }

  /** 6. 保存与同步（仅保存同步当前明细模块） */
  function bindSaveAndSync() {
    const saveBtn = element('btnApplyCurrentWorkbench');
    saveBtn?.addEventListener('click', saveAndApplyCurrentModule);
  }

  /** 7. 预设与真实词库扫描 */
  function bindPresetAndScan() {
    element('btnScanClient')?.addEventListener('click', scanClientDictionary);

    // 客户端显式异步扫描触发（传言、宝箱、字体）
    element('btnScanChests')?.addEventListener('click', () => {
      element('scanChestsSpinner')?.classList.remove('hidden');
      showToast("正在读取客户端宝箱数据（chests.datc64）...");
      post('scanClientChests');
    });

    element('btnScanRumours')?.addEventListener('click', () => {
      element('scanRumoursSpinner')?.classList.remove('hidden');
      showToast("正在读取客户端地图传言数据（GGPK/Bundle）...");
      post('scanClientRumours');
    });

    element('btnScanFontSettings')?.addEventListener('click', () => {
      element('scanFontSpinner')?.classList.remove('hidden');
      showToast("正在读取客户端生效字体模板（uisettings*.xml）...");
      post('scanClientFontSettings');
    });
    element('btnRestoreSnapshot')?.addEventListener('click', () => {
      if (!lastSnapshot || lastSnapshot.length === 0) {
        showToast("⚠️ 暂无已保存的生效快照，当前已是最新规则");
        return;
      }
      colorModData = JSON.parse(JSON.stringify(lastSnapshot));
      renderTable();
      renderLiveCard();
      showToast("已恢复上一次生效的快照！");
    });

    // 预设切换、另存为与删除
    element('presetSelect')?.addEventListener('change', e => onPresetChange(e.target.value));
    element('btnSavePresetModal')?.addEventListener('click', () => {
      if (element('inpNewPresetName')) element('inpNewPresetName').value = "";
      if (element('savePresetError')) element('savePresetError').innerText = "";
      element('savePresetModal')?.classList.remove('hidden');
    });
    element('btnCloseSavePresetModal')?.addEventListener('click', () => element('savePresetModal')?.classList.add('hidden'));
    element('btnCancelSavePreset')?.addEventListener('click', () => element('savePresetModal')?.classList.add('hidden'));
    element('btnConfirmSavePreset')?.addEventListener('click', () => {
      const name = element('inpNewPresetName')?.value;
      const err = saveCustomPreset(name);
      if (err) {
        if (element('savePresetError')) element('savePresetError').innerText = err;
      } else {
        element('savePresetModal')?.classList.add('hidden');
      }
    });
    element('btnDeletePreset')?.addEventListener('click', deleteCurrentCustomPreset);
  }

  /** 8. 词缀染色过滤与右侧仿真视窗联动 */
  function bindColorModFilters() {
    ['通用', '地图', '石板', '终局地图'].forEach(grp => {
      element('pill-' + grp)?.addEventListener('click', () => {
        currentGroup = grp;
        colorModCurrentPage = 1;
        document.querySelectorAll('.pill-btn').forEach(b => b.classList.remove('active'));
        element('pill-' + grp)?.classList.add('active');

        // 自动联动右侧 Tooltip 卡片仿真类型
        if (grp === '通用') {
          previewType = 'gear';
        } else if (grp === '石板') {
          previewType = 'tablet';
        } else if (grp === '终局地图') {
          previewType = 'endgame';
        } else {
          previewType = 'map';
        }
        const sel = element('cardTypeSelect');
        if (sel) {
          sel.value = previewType === 'endgame' ? 'map' : previewType;
        }
        renderTable();
        renderLiveCard();
      });
    });
    element('searchInput')?.addEventListener('input', () => {
      colorModCurrentPage = 1;
      renderTable();
    });

    // 批量全选 / 全不选（框选）
    element('chkSelectAllRules')?.addEventListener('change', e => toggleSelectAll(e.target.checked));
    element('btnBatchCheckAll')?.addEventListener('click', () => toggleSelectAll(true));
    element('btnBatchUncheckAll')?.addEventListener('click', () => toggleSelectAll(false));

    // 分页操作
    element('btnFirstPage')?.addEventListener('click', () => {
      colorModCurrentPage = 1;
      renderTable();
    });
    element('btnPrevPage')?.addEventListener('click', () => {
      if (colorModCurrentPage > 1) {
        colorModCurrentPage--;
        renderTable();
      }
    });
    element('btnNextPage')?.addEventListener('click', () => {
      colorModCurrentPage++;
      renderTable();
    });
    element('btnLastPage')?.addEventListener('click', () => {
      colorModCurrentPage = 999999;
      renderTable();
    });

    element('btnBatchWhole')?.addEventListener('click', () => {
      colorModData.forEach(i => { if (i.group === currentGroup) i.style = 'Whole'; });
      commitView();
      renderTable();
      renderLiveCard();
      showToast("已批量设为整句着色");
    });
    element('btnBatchNumber')?.addEventListener('click', () => {
      colorModData.forEach(i => { if (i.group === currentGroup) i.style = 'NumberOnly'; });
      commitView();
      renderTable();
      renderLiveCard();
      showToast("已批量设为仅数字着色");
    });
    element('btnScanIconPatch')?.addEventListener('click', () => {
      element("iconPatchSpinner")?.classList.remove("hidden");
      const status = element("iconPatchStatusText");
      if (status) status.innerText = "读取中...";
      post("scanIconPatch", {});
    });
    element('btnRestoreIconPatch')?.addEventListener('click', restoreIconPatch);
    element('iconPatchSearch')?.addEventListener('input', () => renderIconPatchList());
    element('btnIconSelectAll')?.addEventListener('click', () => {
      getFilteredIconEntries().forEach(e => { e.enabled = true; });
      renderIconPatchList();
    });
    element('btnIconSelectNone')?.addEventListener('click', () => {
      getFilteredIconEntries().forEach(e => { e.enabled = false; });
      renderIconPatchList();
    });

    element('cardTypeSelect')?.addEventListener('change', e => {
      previewType = e.target.value;
      renderLiveCard();
    });
  }

  /** 10. 字体预设、细分字号与加粗交互 */
  function bindFontControls() {
    element('fontPresetSelect')?.addEventListener('change', e => onFontPresetChange(e.target.value));

    element('btnSaveFontPreset')?.addEventListener('click', () => {
      const name = prompt("请输入自定义排版预设名称：", "个性排版预设");
      if (name !== null) {
        const err = saveCustomFontPreset(name);
        if (err) alert(err);
      }
    });

    element('btnDeleteFontPreset')?.addEventListener('click', deleteCurrentCustomFontPreset);

    element('btnResetFontPreset')?.addEventListener('click', () => onFontPresetChange('default'));

    element('fontSizeDropItemSelect')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSizeDropItemSelect');
      showToast(`掉落物与物品字号：${sel?.options[sel.selectedIndex]?.text || sel?.value}`);
    });

    element('fontSizeGeneralSelect')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSizeGeneralSelect');
      showToast(`通用正文描述字号：${sel?.options[sel.selectedIndex]?.text || sel?.value}`);
    });

    element('fontSizeTitleSelect')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSizeTitleSelect');
      showToast(`界面窗口大标题字号：${sel?.options[sel.selectedIndex]?.text || sel?.value}`);
    });

    element('fontSizeTinySelect')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSizeTinySelect');
      showToast(`微型数值角标字号：${sel?.options[sel.selectedIndex]?.text || sel?.value}`);
    });

    element('fontSelectDropdown')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSelectDropdown');
      const selName = sel?.options[sel.selectedIndex]?.text || sel?.value;
      showToast(`已选用字体：${selName}`);
    });

    element('fontSizeDeltaSelect')?.addEventListener('change', () => {
      updateFontPreview();
      const sel = element('fontSizeDeltaSelect');
      const selText = sel?.options[sel.selectedIndex]?.text || sel?.value;
      showToast(`已设置全局整平档位：${selText}`);
    });

    element('chkFontBold')?.addEventListener('change', () => {
      updateFontPreview();
      const isBold = element('chkFontBold')?.checked;
      showToast(isBold ? "已启用字体原生加粗 (Weight 700)" : "已还原字体默认粗细");
    });
  }

  /** 11. 价格设置 */
  function bindPriceControls() {
    // priceSepSelect 的 change 已由下方批量绑定统一处理（那里还会使预览失效），
    // 此处不再单独绑定，否则同一个 change 会触发两次回调。
    element('btnPreviewPricePatch')?.addEventListener('click', previewPrice);

    // 清除已写入客户端的价格后缀（后端 CleanPricePatch，走正则剥离）
    element('btnCleanPricePatch')?.addEventListener('click', () => {
      if (!confirm('确定要清除已写入客户端的价格后缀吗？\n\n这会把物品名恢复为纯名称（如「混沌石 [150C]」→「混沌石」），\n物品名本身的汉化不受影响。')) return;
      const sep = element('priceSepSelect')?.value ?? ' ';
      showToast('正在清除客户端价格后缀...');
      showEffectOperationOverlay('正在清除价格后缀，写入期间请勿关闭窗口...', 60);
      post('cleanPricePatch', { separator: sep });
    });

    element('btnPriceSelectAll')?.addEventListener('click', () => {
      Object.keys(PRICE_CATEGORY_INPUTS).forEach(id => {
        const el = element(id);
        if (el) el.checked = true;
      });
      invalidatePendingPricePreview();
      if (currentPreviewItems && currentPreviewItems.length > 0) {
        renderPricePreviewTable(element('pricePreviewSearch')?.value || '');
      }
      showToast("已全选全部 14 个资产分类");
    });

    element('btnPriceUnselectAll')?.addEventListener('click', () => {
      Object.keys(PRICE_CATEGORY_INPUTS).forEach(id => {
        const el = element(id);
        if (el) el.checked = false;
      });
      invalidatePendingPricePreview();
      if (currentPreviewItems && currentPreviewItems.length > 0) {
        renderPricePreviewTable(element('pricePreviewSearch')?.value || '');
      }
      showToast("已取消勾选全部分类");
    });

    element('btnPriceSelectCommon')?.addEventListener('click', () => {
      const commonIds = ['chkPriceCurrency', 'chkPriceLineage', 'chkPriceRunes', 'chkPriceEssences', 'chkPriceSoulCores', 'chkPriceUncutGems'];
      Object.keys(PRICE_CATEGORY_INPUTS).forEach(id => {
        const el = element(id);
        if (el) el.checked = commonIds.includes(id);
      });
      invalidatePendingPricePreview();
      if (currentPreviewItems && currentPreviewItems.length > 0) {
        renderPricePreviewTable(element('pricePreviewSearch')?.value || '');
      }
      showToast("已选用推荐常用资产分类 (通货/血脉/符文/精华/核心/未切割宝石)");
    });

    element('pricePreviewSearch')?.addEventListener('input', e => {
      renderPricePreviewTable(e.target.value);
    });

    Object.keys(PRICE_CATEGORY_INPUTS).concat(['priceFilterMin', 'priceSepSelect', 'chkPriceStripExisting']).forEach(id => {
      element(id)?.addEventListener('change', () => {
        // 这些参数直接决定预览计划的生成结果。一旦变更，此前确认的预览即失效，
        // 必须连同其 token 一并丢弃，否则点保存会把旧计划写进客户端，与界面当前选择不符。
        invalidatePendingPricePreview();
        updatePriceSim();
      });
    });
  }

  /** 12. 传言搜索 */
  function bindRumourSearch() {
    element('rumourSearch')?.addEventListener('input', renderRumoursTable);
  }

  /** 13. 模态框打开与关闭 */
  function bindModalControls() {
    element('btnOpenAddModal')?.addEventListener('click', () => {
      if (element('modalRuleText')) element('modalRuleText').value = "";
      element('autocompleteDropdown')?.classList.add('hidden');
      updateModalBox();
      element('addCustomModal')?.classList.remove('hidden');
    });
    element('btnCloseAddModal')?.addEventListener('click', () => element('addCustomModal')?.classList.add('hidden'));
    element('btnCancelModal')?.addEventListener('click', () => element('addCustomModal')?.classList.add('hidden'));
    element('modalRuleText')?.addEventListener('input', e => onModalInput(e.target.value));
    element('modalRuleStyle')?.addEventListener('change', updateModalBox);
    element('modalRuleColor')?.addEventListener('input', updateModalBox);
    element('btnConfirmAddRule')?.addEventListener('click', () => {
      const text = element('modalRuleText')?.value.trim();
      if (!text) { alert('请输入词缀内容'); return; }
      const grp = element('modalRuleGroup')?.value || '地图';
      const st = element('modalRuleStyle')?.value || 'Whole';
      const col = element('modalRuleColor')?.value || '#e64545';
      const tag = element('modalRuleTag')?.value.trim() || '自定义';

      colorModData.unshift({
        id: 'custom_' + Date.now(),
        text,
        group: grp,
        color: col,
        style: st,
        tag,
        isCustom: true,
        enabled: true
      });

      element('addCustomModal')?.classList.add('hidden');
      renderTable();
      showToast("🎉 自定义规则添加成功！");
    });

    // 模态框快捷模板
    element('tplReflect')?.addEventListener('click', () => fillModal('玩家受到 (15-20)% 的物理伤害反射', '地图', 'Whole', '#e64545', '高危反伤'));
    element('tplNoRegen')?.addEventListener('click', () => fillModal('玩家无法回复生命或魔力', '地图', 'Whole', '#e64545', '高危恶疾'));
    element('tplInstantLeech')?.addEventListener('click', () => fillModal('偷取立即生效', '通用', 'Whole', '#38b469', '核心机制'));
    element('tplImmuneChaos')?.addEventListener('click', () => fillModal('怪物免疫混沌伤害', '石板', 'Whole', '#e64545', '高危免疫'));

    // 导入/导出
    element('btnOpenImportModal')?.addEventListener('click', () => {
      element('ioModalTitle').innerText = '📥 导入补丁规则配置码';
      element('ioTextarea').value = '';
      element('ioModal')?.classList.remove('hidden');
    });
    element('btnOpenExportModal')?.addEventListener('click', () => {
      element('ioModalTitle').innerText = '📤 导出当前规则配置码';
      element('ioTextarea').value = JSON.stringify(colorModData, null, 2);
      element('ioModal')?.classList.remove('hidden');
    });
    element('btnCloseIoModal')?.addEventListener('click', () => element('ioModal')?.classList.add('hidden'));
    element('btnCancelIo')?.addEventListener('click', () => element('ioModal')?.classList.add('hidden'));
    element('btnConfirmIo')?.addEventListener('click', () => {
      const val = element('ioTextarea')?.value.trim();
      if (element('ioModalTitle')?.innerText.includes('导入')) {
        try {
          const parsed = JSON.parse(val);
          if (Array.isArray(parsed)) {
            // 导入会整体替换当前规则，用户未保存的编辑将丢失，必须先确认。
            if (colorModData.length > 0 &&
                !confirm(`导入将用 ${parsed.length} 条规则覆盖当前的 ${colorModData.length} 条规则，且无法撤销。\n\n确定继续吗？`)) {
              return;
            }
            colorModData = parsed;
            renderTable();
            showToast("🎉 成功导入规则！");
            element('ioModal')?.classList.add('hidden');
          } else {
            alert("格式不符合规则数组规范");
          }
        } catch (e) {
          alert("配置码解析失败：" + e.message);
        }
      } else {
        // 剪贴板写入是异步且可能失败（权限被拒、文档失焦）。
        // 必须等结果再提示，否则用户粘贴出来是旧内容却以为复制成功。
        copyToClipboard(val)
          .then(() => {
            showToast("已复制配置码到剪贴板！");
            element('ioModal')?.classList.add('hidden');
          })
          .catch(err => {
            showToast("复制失败：" + (err && err.message ? err.message : "浏览器拒绝了剪贴板访问") , true);
          });
      }
    });
  }

  /** 14. 游戏特效与性能优化 (EffectPatch) 事件绑定 */
  function bindEffectPatchControls() {
    element('btnApplyEffectRules')?.addEventListener('click', applyAllEffectSettings);
    element('btnRestoreEffectRules')?.addEventListener('click', restoreAllEffectSettings);
    element('btnQuickApplyEffectPatch')?.addEventListener('click', (e) => {
      e.stopPropagation();
      applyAllEffectSettings();
    });

    // 技能专属设置弹窗控制
    element('btnOpenSpellBlockSettings')?.addEventListener('click', (e) => {
      e.stopPropagation();
      element('spellBlockSettingsModal')?.classList.remove('hidden');
      renderSpellBlockModal();
    });
    element('btnCloseSpellBlockModal')?.addEventListener('click', () => {
      element('spellBlockSettingsModal')?.classList.add('hidden');
      renderEffectPatchUi();
    });
    element('btnSaveSpellBlockModal')?.addEventListener('click', () => {
      element('spellBlockSettingsModal')?.classList.add('hidden');
      // 「应用屏蔽」：把弹窗里勾选的技能真正写入客户端。
      // 早期这里只关窗，导致 applySpellBlocks 无任何可达入口。
      applySpellBlocks();
    });
    element('spellBlockSettingsModal')?.addEventListener('click', (e) => {
      if (e.target === element('spellBlockSettingsModal')) {
        element('spellBlockSettingsModal')?.classList.add('hidden');
        renderEffectPatchUi();
      }
    });

    // 技能设置弹窗内部快捷操作
    element('spellSearchInput')?.addEventListener('input', (e) => {
      spellSearchKeyword = e.target.value;
      renderSpellBlockModal();
    });
    element('btnSpellFilterOnlyChecked')?.addEventListener('click', () => {
      spellFilterOnlyChecked = !spellFilterOnlyChecked;
      renderSpellBlockModal();
    });
    element('btnSpellSelectAllVisible')?.addEventListener('click', () => {
      const kw = (spellSearchKeyword || "").trim().toLowerCase();
      const visibleSpells = POE2_SPELL_OPTIONS.filter(opt => {
        if (!kw) return true;
        return opt.name.toLowerCase().includes(kw) || opt.value.toLowerCase().includes(kw);
      });
      visibleSpells.forEach(opt => selectedBlockedSpells.add(opt.value));
      renderSpellBlockModal();
    });
    element('btnSpellClearAll')?.addEventListener('click', () => {
      selectedBlockedSpells.clear();
      renderSpellBlockModal();
    });
    element('btnRestoreSpellBlocksInModal')?.addEventListener('click', () => {
      restoreSpellBlocks();
    });

    // 泥人专属设置弹窗控制
    element('btnOpenMudManSettings')?.addEventListener('click', (e) => {
      e.stopPropagation();
      element('mudManSettingsModal')?.classList.remove('hidden');
    });
    element('btnCloseMudManModal')?.addEventListener('click', () => {
      element('mudManSettingsModal')?.classList.add('hidden');
      renderMudManUi();
    });
    element('btnSaveMudManModal')?.addEventListener('click', () => {
      element('mudManSettingsModal')?.classList.add('hidden');
      renderMudManUi();
    });
    element('mudManSettingsModal')?.addEventListener('click', (e) => {
      if (e.target === element('mudManSettingsModal')) {
        element('mudManSettingsModal')?.classList.add('hidden');
        renderMudManUi();
      }
    });
    element('btnRestoreMudManInModal')?.addEventListener('click', () => {
      restoreMudMan();
    });

    // 头顶血条开关
    element('chkOpt-healthbar')?.addEventListener('change', (e) => {
      toggleHealthBar(e.target.checked);
    });
    // 泥人外层卡片主开关与档位单选
    element('chkOpt-mudman')?.addEventListener('change', (e) => {
      if (e.target.checked) {
        if (selectedMudManTier < 0) selectedMudManTier = 4; // 默认推荐中级 + 材质压缩
      } else {
        selectedMudManTier = -1;
      }
      renderMudManUi();
    });
    document.querySelectorAll('.mudman-tier-radio').forEach(radio => {
      radio.addEventListener('change', () => {
        const tier = parseInt(radio.value, 10);
        if (!Number.isNaN(tier)) {
          selectedMudManTier = tier;
          const chkMud = element('chkOpt-mudman');
          if (chkMud) chkMud.checked = true;
        }
        renderMudManUi();
      });
    });

    // 环境去黑暗与全局亮度调整设置弹窗控制
    element('btnOpenRemoveDarkSettings')?.addEventListener('click', (e) => {
      e.stopPropagation();
      element('removeDarkSettingsModal')?.classList.remove('hidden');
    });
    element('btnCloseRemoveDarkModal')?.addEventListener('click', () => {
      element('removeDarkSettingsModal')?.classList.add('hidden');
      renderEffectPatchUi();
    });
    element('btnSaveRemoveDarkModal')?.addEventListener('click', () => {
      element('removeDarkSettingsModal')?.classList.add('hidden');
      renderEffectPatchUi();
    });
    element('removeDarkSettingsModal')?.addEventListener('click', (e) => {
      if (e.target === element('removeDarkSettingsModal')) {
        element('removeDarkSettingsModal')?.classList.add('hidden');
        renderEffectPatchUi();
      }
    });
    document.querySelectorAll('.remove-dark-level-radio').forEach(radio => {
      radio.addEventListener('change', () => {
        selectedRemoveDarkLevel = radio.value;
        localStorage.setItem('frei_remove_dark_level', selectedRemoveDarkLevel);
        Array.from(selectedEffectRules)
          .filter(x => x === 'remove_dark' || x.startsWith('remove_dark:') || x === 'dark_light_objects')
          .forEach(x => selectedEffectRules.delete(x));
        selectedEffectRules.add(`remove_dark:${selectedRemoveDarkLevel}`);
        const chkDark = element('chkOpt-remove_dark');
        if (chkDark) chkDark.checked = true;
        renderEffectPatchUi();
      });
    });

    // ── 小地图颜色设置弹窗控制与联动 ──
    function rgbToHex(r, g, b) {
      const toHex = c => Math.min(255, Math.max(0, Math.round(c))).toString(16).padStart(2, '0');
      return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
    }
    function hexToRgb(hex) {
      const m = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
      return m ? [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)] : [0, 0, 0];
    }

    function syncMinimapColorInputsToUi(c) {
      if (element('mmExploredR')) element('mmExploredR').value = c.exploredR;
      if (element('valExploredR')) element('valExploredR').innerText = c.exploredR;
      if (element('mmExploredG')) element('mmExploredG').value = c.exploredG;
      if (element('valExploredG')) element('valExploredG').innerText = c.exploredG;
      if (element('mmExploredB')) element('mmExploredB').value = c.exploredB;
      if (element('valExploredB')) element('valExploredB').innerText = c.exploredB;
      if (element('mmExploredA')) element('mmExploredA').value = c.exploredA;
      if (element('valExploredA')) element('valExploredA').innerText = c.exploredA;
      if (element('mmColorExploredPicker')) element('mmColorExploredPicker').value = rgbToHex(c.exploredR, c.exploredG, c.exploredB);

      if (element('mmBorderR')) element('mmBorderR').value = c.borderR;
      if (element('valBorderR')) element('valBorderR').innerText = c.borderR;
      if (element('mmBorderG')) element('mmBorderG').value = c.borderG;
      if (element('valBorderG')) element('valBorderG').innerText = c.borderG;
      if (element('mmBorderB')) element('mmBorderB').value = c.borderB;
      if (element('valBorderB')) element('valBorderB').innerText = c.borderB;
      if (element('mmBorderA')) element('mmBorderA').value = c.borderA;
      if (element('valBorderA')) element('valBorderA').innerText = c.borderA;
      if (element('mmColorBorderPicker')) element('mmColorBorderPicker').value = rgbToHex(c.borderR, c.borderG, c.borderB);

      if (element('mmExploringR')) element('mmExploringR').value = c.exploringR;
      if (element('valExploringR')) element('valExploringR').innerText = c.exploringR;
      if (element('mmExploringG')) element('mmExploringG').value = c.exploringG;
      if (element('valExploringG')) element('valExploringG').innerText = c.exploringG;
      if (element('mmExploringB')) element('mmExploringB').value = c.exploringB;
      if (element('valExploringB')) element('valExploringB').innerText = c.exploringB;
      if (element('mmExploringA')) element('mmExploringA').value = c.exploringA;
      if (element('valExploringA')) element('valExploringA').innerText = c.exploringA;
      if (element('mmColorExploringPicker')) element('mmColorExploringPicker').value = rgbToHex(c.exploringR, c.exploringG, c.exploringB);

      if (element('mmBorderGlow')) element('mmBorderGlow').value = c.borderGlow;
      if (element('valBorderGlow')) element('valBorderGlow').innerText = c.borderGlow;
      if (element('mmExploringGlow')) element('mmExploringGlow').value = c.exploringGlow;
      if (element('valExploringGlow')) element('valExploringGlow').innerText = c.exploringGlow;
      if (element('mmVisibilityThreshold')) element('mmVisibilityThreshold').value = c.visibilityThreshold;
      if (element('valVisibilityThreshold')) element('valVisibilityThreshold').innerText = Number(c.visibilityThreshold).toFixed(2);
    }

    function readMinimapColorInputs() {
      return {
        exploredR: parseInt(element('mmExploredR')?.value || '0', 10),
        exploredG: parseInt(element('mmExploredG')?.value || '0', 10),
        exploredB: parseInt(element('mmExploredB')?.value || '0', 10),
        exploredA: parseInt(element('mmExploredA')?.value || '76', 10),
        borderR: parseInt(element('mmBorderR')?.value || '0', 10),
        borderG: parseInt(element('mmBorderG')?.value || '120', 10),
        borderB: parseInt(element('mmBorderB')?.value || '215', 10),
        borderA: parseInt(element('mmBorderA')?.value || '26', 10),
        exploringR: parseInt(element('mmExploringR')?.value || '0', 10),
        exploringG: parseInt(element('mmExploringG')?.value || '128', 10),
        exploringB: parseInt(element('mmExploringB')?.value || '255', 10),
        exploringA: parseInt(element('mmExploringA')?.value || '128', 10),
        borderGlow: parseFloat(element('mmBorderGlow')?.value || '13'),
        exploringGlow: parseFloat(element('mmExploringGlow')?.value || '1'),
        visibilityThreshold: parseFloat(element('mmVisibilityThreshold')?.value || '0.18')
      };
    }

    function bindRgbChannel(sliderId, textId, pickerId, getChannels) {
      const slider = element(sliderId);
      slider?.addEventListener('input', () => {
        if (element(textId)) element(textId).innerText = slider.value;
        const [r, g, b] = getChannels();
        const picker = element(pickerId);
        if (picker) picker.value = rgbToHex(r, g, b);
      });
    }

    bindRgbChannel('mmExploredR', 'valExploredR', 'mmColorExploredPicker', () => [
      parseInt(element('mmExploredR')?.value || '0', 10),
      parseInt(element('mmExploredG')?.value || '0', 10),
      parseInt(element('mmExploredB')?.value || '0', 10)
    ]);
    bindRgbChannel('mmExploredG', 'valExploredG', 'mmColorExploredPicker', () => [
      parseInt(element('mmExploredR')?.value || '0', 10),
      parseInt(element('mmExploredG')?.value || '0', 10),
      parseInt(element('mmExploredB')?.value || '0', 10)
    ]);
    bindRgbChannel('mmExploredB', 'valExploredB', 'mmColorExploredPicker', () => [
      parseInt(element('mmExploredR')?.value || '0', 10),
      parseInt(element('mmExploredG')?.value || '0', 10),
      parseInt(element('mmExploredB')?.value || '0', 10)
    ]);
    element('mmExploredA')?.addEventListener('input', (e) => {
      if (element('valExploredA')) element('valExploredA').innerText = e.target.value;
    });
    element('mmColorExploredPicker')?.addEventListener('input', (e) => {
      const [r, g, b] = hexToRgb(e.target.value);
      if (element('mmExploredR')) element('mmExploredR').value = r;
      if (element('valExploredR')) element('valExploredR').innerText = r;
      if (element('mmExploredG')) element('mmExploredG').value = g;
      if (element('valExploredG')) element('valExploredG').innerText = g;
      if (element('mmExploredB')) element('mmExploredB').value = b;
      if (element('valExploredB')) element('valExploredB').innerText = b;
    });

    bindRgbChannel('mmBorderR', 'valBorderR', 'mmColorBorderPicker', () => [
      parseInt(element('mmBorderR')?.value || '0', 10),
      parseInt(element('mmBorderG')?.value || '120', 10),
      parseInt(element('mmBorderB')?.value || '215', 10)
    ]);
    bindRgbChannel('mmBorderG', 'valBorderG', 'mmColorBorderPicker', () => [
      parseInt(element('mmBorderR')?.value || '0', 10),
      parseInt(element('mmBorderG')?.value || '120', 10),
      parseInt(element('mmBorderB')?.value || '215', 10)
    ]);
    bindRgbChannel('mmBorderB', 'valBorderB', 'mmColorBorderPicker', () => [
      parseInt(element('mmBorderR')?.value || '0', 10),
      parseInt(element('mmBorderG')?.value || '120', 10),
      parseInt(element('mmBorderB')?.value || '215', 10)
    ]);
    element('mmBorderA')?.addEventListener('input', (e) => {
      if (element('valBorderA')) element('valBorderA').innerText = e.target.value;
    });
    element('mmColorBorderPicker')?.addEventListener('input', (e) => {
      const [r, g, b] = hexToRgb(e.target.value);
      if (element('mmBorderR')) element('mmBorderR').value = r;
      if (element('valBorderR')) element('valBorderR').innerText = r;
      if (element('mmBorderG')) element('mmBorderG').value = g;
      if (element('valBorderG')) element('valBorderG').innerText = g;
      if (element('mmBorderB')) element('mmBorderB').value = b;
      if (element('valBorderB')) element('valBorderB').innerText = b;
    });

    bindRgbChannel('mmExploringR', 'valExploringR', 'mmColorExploringPicker', () => [
      parseInt(element('mmExploringR')?.value || '0', 10),
      parseInt(element('mmExploringG')?.value || '128', 10),
      parseInt(element('mmExploringB')?.value || '255', 10)
    ]);
    bindRgbChannel('mmExploringG', 'valExploringG', 'mmColorExploringPicker', () => [
      parseInt(element('mmExploringR')?.value || '0', 10),
      parseInt(element('mmExploringG')?.value || '128', 10),
      parseInt(element('mmExploringB')?.value || '255', 10)
    ]);
    bindRgbChannel('mmExploringB', 'valExploringB', 'mmColorExploringPicker', () => [
      parseInt(element('mmExploringR')?.value || '0', 10),
      parseInt(element('mmExploringG')?.value || '128', 10),
      parseInt(element('mmExploringB')?.value || '255', 10)
    ]);
    element('mmExploringA')?.addEventListener('input', (e) => {
      if (element('valExploringA')) element('valExploringA').innerText = e.target.value;
    });
    element('mmColorExploringPicker')?.addEventListener('input', (e) => {
      const [r, g, b] = hexToRgb(e.target.value);
      if (element('mmExploringR')) element('mmExploringR').value = r;
      if (element('valExploringR')) element('valExploringR').innerText = r;
      if (element('mmExploringG')) element('mmExploringG').value = g;
      if (element('valExploringG')) element('valExploringG').innerText = g;
      if (element('mmExploringB')) element('mmExploringB').value = b;
      if (element('valExploringB')) element('valExploringB').innerText = b;
    });

    element('mmBorderGlow')?.addEventListener('input', (e) => {
      if (element('valBorderGlow')) element('valBorderGlow').innerText = e.target.value;
    });
    element('mmExploringGlow')?.addEventListener('input', (e) => {
      if (element('valExploringGlow')) element('valExploringGlow').innerText = e.target.value;
    });
    element('mmVisibilityThreshold')?.addEventListener('input', (e) => {
      if (element('valVisibilityThreshold')) element('valVisibilityThreshold').innerText = Number(e.target.value).toFixed(2);
    });

    element('btnOpenMinimapColorModal')?.addEventListener('click', (e) => {
      e.stopPropagation();
      syncMinimapColorInputsToUi(currentMinimapColor);
      element('minimapColorModal')?.classList.remove('hidden');
    });
    element('btnCloseMinimapColorModal')?.addEventListener('click', () => {
      element('minimapColorModal')?.classList.add('hidden');
    });
    element('btnResetMinimapColor')?.addEventListener('click', () => {
      syncMinimapColorInputsToUi(DEFAULT_MINIMAP_COLOR);
    });
    element('btnSaveMinimapColor')?.addEventListener('click', () => {
      currentMinimapColor = readMinimapColorInputs();
      localStorage.setItem('frei_minimap_color', JSON.stringify(currentMinimapColor));
      const chkMinimap = element('chkOpt-minimap_visibility');
      if (chkMinimap && chkMinimap.checked) {
        Array.from(selectedEffectRules)
          .filter(x => x === 'minimap_visibility' || x.startsWith('minimap_color:') || x.startsWith('minimap_visibility:'))
          .forEach(x => selectedEffectRules.delete(x));
        selectedEffectRules.add(formatMinimapColorRule(currentMinimapColor));
      }
      element('minimapColorModal')?.classList.add('hidden');
      renderEffectPatchUi();
    });
    element('minimapColorModal')?.addEventListener('click', (e) => {
      if (e.target === element('minimapColorModal')) {
        element('minimapColorModal')?.classList.add('hidden');
      }
    });

    // 视距缩放倍率切换监听
    element('selViewDistanceRatio')?.addEventListener('change', (e) => {
      const val = e.target.value;
      const hasVd = Array.from(selectedEffectRules).some(r => r.startsWith("view_distance"));
      if (hasVd) {
        Array.from(selectedEffectRules).filter(r => r.startsWith("view_distance")).forEach(r => selectedEffectRules.delete(r));
        selectedEffectRules.add(`view_distance:${val}`);
      }
    });

    // 规则复选框改动监听
    document.querySelectorAll('.effect-rule-checkbox').forEach(chk => {
      chk.addEventListener('change', () => {
        const rules = chk.dataset.rule.split(',');
        rules.forEach(r => {
          const ruleName = r.trim();
          if (chk.checked) {
            // 勾选前清掉同名历史变体，避免集合里同时存在
            // "view_distance" 与 "view_distance:2.0"，下发时出现重复条目。
            if (ruleName === 'view_distance') {
              Array.from(selectedEffectRules)
                .filter(x => x === 'view_distance' || x.startsWith('view_distance:'))
                .forEach(x => selectedEffectRules.delete(x));
            }
            if (ruleName === 'remove_dark') {
              Array.from(selectedEffectRules)
                .filter(x => x === 'remove_dark' || x.startsWith('remove_dark:') || x === 'dark_light_objects')
                .forEach(x => selectedEffectRules.delete(x));
              selectedEffectRules.add(`remove_dark:${selectedRemoveDarkLevel}`);
              return;
            }
            if (ruleName === 'minimap_visibility') {
              Array.from(selectedEffectRules)
                .filter(x => x === 'minimap_visibility' || x.startsWith('minimap_color:') || x.startsWith('minimap_visibility:'))
                .forEach(x => selectedEffectRules.delete(x));
              selectedEffectRules.add(formatMinimapColorRule(currentMinimapColor));
              return;
            }
            selectedEffectRules.add(ruleName);
            return;
          }
          // 取消勾选必须按前缀清理。
          // view_distance 存进集合时会被写成 "view_distance:2.0"（带倍率后缀），
          // 直接 delete("view_distance") 删不掉，紧接着 renderEffectPatchUi 又按
          // 「集合里有 view_distance 前缀」把它勾回去 —— 表现为这个开关取消不了。
          if (ruleName === 'view_distance') {
            Array.from(selectedEffectRules)
              .filter(x => x === 'view_distance' || x.startsWith('view_distance:'))
              .forEach(x => selectedEffectRules.delete(x));
            return;
          }
          if (ruleName === 'remove_dark') {
            Array.from(selectedEffectRules)
              .filter(x => x === 'remove_dark' || x.startsWith('remove_dark:') || x === 'dark_light_objects')
              .forEach(x => selectedEffectRules.delete(x));
            return;
          }
          if (ruleName === 'minimap_visibility') {
            Array.from(selectedEffectRules)
              .filter(x => x === 'minimap_visibility' || x.startsWith('minimap_color:') || x.startsWith('minimap_visibility:'))
              .forEach(x => selectedEffectRules.delete(x));
            return;
          }
          selectedEffectRules.delete(ruleName);
        });
        renderEffectPatchUi();
      });
    });

    // 技能总开关监听
    element('chkOpt-spell_block_master')?.addEventListener('change', (e) => {
      if (e.target.checked) {
        if (selectedBlockedSpells.size === 0) {
          selectedBlockedSpells.add("lightning_spark");
        }
      } else {
        selectedBlockedSpells.clear();
      }
      renderEffectPatchUi();
    });

    element('chkSpellIncludeAudio')?.addEventListener('change', e => {
      spellIncludeAudio = e.target.checked;
    });

    // 初始化所有表格与视图
    loadCustomPresets();
    renderTable();
    renderMapBeam();
    renderChestsList();
    renderRumoursTable();
    updatePriceSim();
    renderEffectPatchUi();

    // MTX 外观模块事件绑定
    document.querySelectorAll(".mtx-tab-btn").forEach(btn => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".mtx-tab-btn").forEach(b => b.classList.remove("active"));
        btn.classList.add("active");
        mtxCurrentTab = btn.dataset.tab || "all";
        renderMtxGrid();
      });
    });

    element("txtMtxSearch")?.addEventListener("input", e => {
      mtxSearchQuery = (e.target.value || "").trim();
      renderMtxGrid();
    });

    element("btnClearMtxSearch")?.addEventListener("click", () => {
      const inp = element("txtMtxSearch");
      if (inp) inp.value = "";
      mtxSearchQuery = "";
      renderMtxGrid();
    });

    element("btnApplyMtxPatch")?.addEventListener("click", applyMtxPatchAction);
    element("btnQuickApplyMtxPatch")?.addEventListener("click", e => {
      e.stopPropagation();
      applyMtxPatchAction();
    });
    element("btnRestoreMtxPatch")?.addEventListener("click", restoreMtxPatchAction);

    element("btnClearAllMtxSelections")?.addEventListener("click", () => {
      selectedArmourSet = null;
      selectedPortal = null;
      selectedPet = null;
      selectedMtxNames.clear();
      renderMtxSummary();
      renderMtxGrid();
    });

    element("selMtxCharacterClass")?.addEventListener("change", e => {
      selectedMtxClass = e.target.value;
    });

    renderMtxUi();
  }

  window.patcherUi = {
    hideOverlay: hideEffectOperationOverlay,
    receiveBusyState,
    initialize() {
      // 事件绑定按功能区拆到独立方法，避免单函数过长。
      // 各段互不依赖，任一段出问题不影响其余功能区的绑定。
      bindHubCards();
      bindModuleMasters();
      bindWorkbenchNavigation();
      bindGameDirInput();
      bindSnapshotControls();
      bindSaveAndSync();
      bindPresetAndScan();
      bindColorModFilters();
      bindFontControls();
      bindPriceControls();
      bindRumourSearch();
      bindModalControls();
      bindEffectPatchControls();
    },

    receive(payload) {
      if (!payload) return;
      state = payload;
      renderStatus();
      if (payload.pricePreview) {
        renderPricePreview(payload.pricePreview);
      }

      // 图标扫描结果
      if (Array.isArray(payload.iconPatchEntries)) {
        iconPatchEntries = payload.iconPatchEntries.map(e => ({ ...e }));
        iconPatchSkipped = payload.iconPatchSkipped || 0;
        iconSpriteCell = payload.iconSpriteCell || 48;
        iconSpriteColumns = payload.iconSpriteColumns || 32;

        element("iconPatchSpinner")?.classList.add("hidden");
        element("iconPatchToolbar")?.classList.remove("hidden");
        element("iconPatchStatusDot")?.classList.add("synced");
        const statusText = element("iconPatchStatusText");
        if (statusText) {
          statusText.innerText = `可用 ${iconPatchEntries.length} 个`
            + (iconPatchSkipped > 0 ? ` (${iconPatchSkipped} 个不适用)` : "");
          statusText.style.color = "#34d399";
        }
        renderIconPatchTabs();
        renderIconPatchList();
      }

      // 0. 版本快照数据动态更新
      if (Array.isArray(payload.snapshots)) {
        renderSnapshots(payload.snapshots);
      }

      // 1. 真实词库扫描返回
      if (payload.scannedEntries && Array.isArray(payload.scannedEntries) && payload.scannedEntries.length > 0) {
        CLIENT_STAT_DICTIONARY = payload.scannedEntries;
        isClientSynced = true;
        const spinner = element("scanSpinner");
        const btnText = element("scanBtnText");
        const btn = element("btnScanClient");
        const badgeText = element("dictStatusText");
        const badgeDot = element("dictStatusDot");

        spinner?.classList.add("hidden");
        if (btnText) btnText.innerText = `✓ 词库已提取 (${payload.scannedEntries.length}条)`;
        if (btn) {
          btn.style.opacity = "1";
          btn.style.borderColor = "#34d399";
          btn.style.color = "#34d399";
        }
        badgeDot?.classList.add("synced");
        if (badgeText) {
          badgeText.innerText = `客户端已提取 (${payload.scannedEntries.length}条挂载)`;
          badgeText.style.color = "#34d399";
        }
        const hint = element("dictSuggestHint");
        if (hint) hint.innerText = `✓ 客户端 ${payload.scannedEntries.length} 条官方词典已挂接主表格与自动补全`;

        // 词缀数据写入 ① clientAffixes：只保留客户端提供的标识与文本，
        // 以及后端算好的默认颜色/样式/启用状态。这是唯一权威的默认值来源。
        clientAffixes = payload.scannedEntries.map((entry, idx) => {
          const statId = entry.statId || ("stat_" + idx);
          const isDanger = !!entry.isDanger;
          return {
            statId,
            displayName: entry.displayName || entry.text || statId,
            group: entry.group || "通用",
            color: entry.color || (isDanger ? "#E3170D" : "#ffffff"),
            // 后端已按词缀文本判定好样式，直接采用；缺字段时才退化为整句
            style: entry.style === "NumberOnly" ? "NumberOnly" : "Whole",
            enabled: !!entry.enabled,
            isDanger,
            currentColorTag: entry.currentColorTag || ""
          };
        });

        // 视图由 ① + 当前预设配置重算，不复制任何快照
        rebuildView();
        colorModCurrentPage = 1;
        renderTable();
        renderLiveCard();
      }

      // 2. 持久化配置回显
      if (state.settings) {
        if (state.settings.colorModEnabled !== undefined) toggleModuleMaster("colormod", state.settings.colorModEnabled);
        if (state.settings.mapBeamEnabled !== undefined) toggleModuleMaster("mapbeam", state.settings.mapBeamEnabled);
        if (state.settings.chestEnhanced !== undefined) toggleModuleMaster("chests", state.settings.chestEnhanced);
        if (state.settings.rumourEnhanced !== undefined) toggleModuleMaster("rumours", state.settings.rumourEnhanced);
        // 图标总开关也要回显，否则关掉后重启会重新显示为勾选。
        if (state.settings.iconPatchEnabled !== undefined) toggleModuleMaster("iconpatch", state.settings.iconPatchEnabled);
        applyPriceSettings(state.settings);

        // 染色样式回显：后端持久化的是 ColorModStyle 字符串
        if (state.settings.colorModStyle) {
          const styleSelect = element("patcherColorModStyle");
          if (styleSelect) styleSelect.value = state.settings.colorModStyle;
        }

        // 词缀染色的用户配置：只保存与默认值不同的字段。
        // 词缀文本与默认样式一律来自客户端实时扫描，这里不涉及任何文本。
        // 旧版本存的是全量快照（含文本），已无法区分哪些是用户改的，直接忽略。
        if (Array.isArray(state.settings.colorModRules) && state.settings.colorModRules.length > 0) {
          const loaded = {};
          for (const r of state.settings.colorModRules) {
            if (!r || !r.id) continue;
            const diff = {};
            if (r.color && r.color.toLowerCase() !== "#000000") diff.color = r.color;
            if (r.style === "NumberOnly") diff.style = "NumberOnly";
            if (r.enabled === false) diff.enabled = false;
            if (Object.keys(diff).length > 0) loaded[r.id] = diff;
          }
          userConfig = new Map(Object.entries(loaded));
          if (currentPresetId === "default" && userConfig.size > 0) {
            // 有用户配置时作为一份独立预设加载，默认预设始终保持纯净
            const id = "preset_loaded";
            customPresets[id] = {
              id,
              name: "上次的配置",
              config: loaded,
              createdAt: Date.now()
            };
            currentPresetId = id;
            renderPresetSelect();
          }
        } else {
          // 后端下发了空配置 —— 可能是「恢复默认设置」。
          // 必须显式清空内存，否则旧配置继续留在 userConfig 里，
          // 用户下次点保存又把旧值写回去，表现为「恢复默认无效」。
          userConfig = new Map();
        }

        // 启动时词缀列表保持为空，等待用户显式点击「提取客户端」
        if (colorModData.length === 0) {
          colorModCurrentPage = 1;
          renderTable();
          renderLiveCard();
        }

        // 回显神图光柱配置
        if (Array.isArray(state.settings.mapBeamTargets) && state.settings.mapBeamTargets.length > 0) {
          state.settings.mapBeamTargets.forEach(t => {
            const it = MAP_BEAM_DATA.find(x => x.name === t.mapName);
            if (it) {
              it.enabled = t.enabled;
              it.color = t.color;
            }
          });
          renderMapBeam();
        } else {
          // 后端无配置（含恢复默认）：把所有条目复位为内置默认值。
          // 否则内存里仍是上次的开关与颜色，界面看起来「没恢复」。
          MAP_BEAM_DATA.forEach(it => {
            it.enabled = true;
            it.color = "Farudin";
          });
          renderMapBeam();
        }

        // 3. 宝箱真实数据装配：只来自客户端实时扫描。
        // 未扫描时保持为空，不展示内置字典——那是历史整理的静态数据，与当前客户端
        // 可能不一致，且展示后可被一并提交写回客户端，等于用过期内容覆盖真实数据。
        if (Array.isArray(payload.clientChests) && payload.clientChests.length > 0) {
          element("scanChestsSpinner")?.classList.add("hidden");
          initChestsFromClient(payload.clientChests);
        }

        // 回显宝箱标注配置 (用户自定义覆盖)
        if (Array.isArray(state.settings.chestItems) && state.settings.chestItems.length > 0) {
          state.settings.chestItems.forEach(c => {
            const it = CHESTS_DATA.find(x => x.originalName === c.originalName);
            if (it) {
              it.enabled = c.enabled;
              if (c.modifiedName) it.modifiedName = c.modifiedName;
            }
          });
          renderChestsList();
        } else {
          // 后端无配置（含恢复默认）：清掉用户自定义名称与开关。
          // modifiedName 回落到原始名，避免残留上次的标注后缀。
          CHESTS_DATA.forEach(it => {
            it.enabled = false;
            it.modifiedName = it.originalName;
          });
          renderChestsList();
        }

        // 4.1 系统字体列表自动枚举填充
        if (Array.isArray(payload.installedFonts) && payload.installedFonts.length > 0) {
          const dropdown = element("fontSelectDropdown");
          const countHint = element("fontCountHint");
          if (countHint) {
            countHint.innerText = `共 ${payload.installedFonts.length} 款系统已安装字体`;
          }
          if (dropdown) {
            const savedFont = state.settings?.fontFamily || "Microsoft YaHei";
            if (dropdown.options.length <= 1) {
              dropdown.innerHTML = "";
              payload.installedFonts.forEach(f => {
                const opt = document.createElement("option");
                const val = f.typefaceName || f.TypefaceName || "";
                opt.value = val;
                opt.text = f.displayName || f.DisplayName || val;
                dropdown.appendChild(opt);
              });
            }
            if (savedFont) {
              let exists = Array.from(dropdown.options).some(o => o.value === savedFont);
              if (!exists) {
                const opt = document.createElement("option");
                opt.value = savedFont;
                opt.text = `${savedFont} (已保存)`;
                dropdown.appendChild(opt);
              }
              dropdown.value = savedFont;
            }
          }
        }

        // 4.2 客户端生效字体配置装配
        if (payload.fontSettings) {
          element("scanFontSpinner")?.classList.add("hidden");
          const fs = payload.fontSettings;
          const badgeText = element("fontSettingsText");
          const badgeDot = element("fontSettingsDot");
          const fileName = (fs.filePath || "").split(/[\\\/]/).pop() || "生效模板";
          if (badgeText) {
            badgeText.innerText = `生效: ${fileName}`;
            badgeText.style.color = "#34d399";
          }
          badgeDot?.classList.add("synced");

          const card = element("clientFontInfoCard");
          if (card) {
            card.classList.remove("hidden");
            if (element("cfiFileName")) element("cfiFileName").innerText = fs.filePath || "-";
            if (element("cfiCurrentTypeface")) element("cfiCurrentTypeface").innerText = fs.currentTypeface || "(默认)";
            const fallbackList = (fs.fallbackFontsCJK && fs.fallbackFontsCJK.length > 0) ? fs.fallbackFontsCJK : fs.fallbackFontsAny;
            if (element("cfiFallbackChain")) element("cfiFallbackChain").innerText = fallbackList ? fallbackList.join(", ") : "原生链条";
          }
        }

        // 4.3 回显排版预设与细分分类字号
        if (state.settings) {
          if (Array.isArray(state.settings.fontCustomPresets) && state.settings.fontCustomPresets.length > 0) {
            customFontPresets = {};
            state.settings.fontCustomPresets.forEach(p => {
              if (p && p.id) {
                customFontPresets[p.id] = {
                  id: p.id,
                  name: p.name || p.id,
                  fontFamily: p.fontFamily || "Microsoft YaHei",
                  globalSizeDelta: p.globalSizeDelta ?? 0,
                  dropItemSizeDelta: p.dropItemSizeDelta ?? 0,
                  generalTextSizeDelta: p.generalTextSizeDelta ?? 0,
                  titleSizeDelta: p.titleSizeDelta ?? 0,
                  tinyBadgeSizeDelta: p.tinyBadgeSizeDelta ?? 0,
                  isBold: !!p.isBold
                };
              }
            });
          }

          if (state.settings.fontPresetId) {
            currentFontPresetId = state.settings.fontPresetId;
          }
          renderFontPresetSelect();

          if (state.settings.fontDropItemSizeDelta !== undefined && state.settings.fontDropItemSizeDelta !== null) {
            setSelectValue("fontSizeDropItemSelect", String(state.settings.fontDropItemSizeDelta));
          }
          if (state.settings.fontGeneralTextSizeDelta !== undefined && state.settings.fontGeneralTextSizeDelta !== null) {
            setSelectValue("fontSizeGeneralSelect", String(state.settings.fontGeneralTextSizeDelta));
          }
          if (state.settings.fontTitleSizeDelta !== undefined && state.settings.fontTitleSizeDelta !== null) {
            setSelectValue("fontSizeTitleSelect", String(state.settings.fontTitleSizeDelta));
          }
          if (state.settings.fontTinyBadgeSizeDelta !== undefined && state.settings.fontTinyBadgeSizeDelta !== null) {
            setSelectValue("fontSizeTinySelect", String(state.settings.fontTinyBadgeSizeDelta));
          }

          const deltaSelect = element("fontSizeDeltaSelect");
          if (deltaSelect && state.settings.fontSizeDelta !== undefined && state.settings.fontSizeDelta !== null) {
            deltaSelect.value = String(state.settings.fontSizeDelta);
          }
          const chkBold = element("chkFontBold");
          if (chkBold && state.settings.fontBold !== undefined && state.settings.fontBold !== null) {
            chkBold.checked = !!state.settings.fontBold;
          }
          updateFontPreview();
        }

        // 5. 回显与装配异界传言配置 (端到端真实闭环)
        let sourceRumours = [];
        if (Array.isArray(payload.clientRumours) && payload.clientRumours.length > 0) {
          element("scanRumoursSpinner")?.classList.add("hidden");
          const rBadgeText = element("rumoursStatusText");
          const rBadgeDot = element("rumoursStatusDot");
          if (rBadgeText) {
            rBadgeText.innerText = `客户端已提取 (${payload.clientRumours.length}条)`;
            rBadgeText.style.color = "#34d399";
          }
          rBadgeDot?.classList.add("synced");
          sourceRumours = payload.clientRumours;
        } else if (Array.isArray(payload.defaultRumours) && payload.defaultRumours.length > 0) {
          sourceRumours = payload.defaultRumours;
        }

        if (sourceRumours.length > 0) {
          const savedRumoursMap = new Map();
          if (Array.isArray(state.settings.rumourItems)) {
            state.settings.rumourItems.forEach(r => {
              if (r.rowIndex !== undefined && r.rowIndex !== null) {
                savedRumoursMap.set(Number(r.rowIndex), r);
              } else if (r.originalText) {
                savedRumoursMap.set(r.originalText, r);
              }
            });
          }

          RUMOURS_DATA = sourceRumours.map(item => {
            const rowIdx = item.rowIndex !== undefined ? item.rowIndex : (item.RowIndex !== undefined ? item.RowIndex : 0);
            const orig = item.originalRumourText || item.OriginalRumourText || item.originalText || item.OriginalText || "";
            const saved = savedRumoursMap.get(rowIdx) || savedRumoursMap.get(orig);

            return {
              rowIndex: rowIdx,
              mapName: item.mapName || item.MapName || "",
              category: item.category || item.Category || "常规",
              color: saved?.color || item.color || item.Color || "#ffffff",
              originalText: orig,
              modifiedText: saved?.modifiedText || item.rumourText || item.RumourText || orig,
              enabled: saved !== undefined ? !!saved.enabled : (item.enabled !== undefined ? !!item.enabled : true)
            };
          });
          renderRumoursTable();
        }

        // 4. 特效与性能优化配置回显
        if (state.settings.effectPatchRuleIds !== undefined) {
          selectedEffectRules = new Set(state.settings.effectPatchRuleIds || []);
        }
        if (state.settings.blockedSpellIds !== undefined) {
          selectedBlockedSpells = new Set(state.settings.blockedSpellIds || []);
        }
        if (state.settings.spellBlockIncludeAudio !== undefined) {
          spellIncludeAudio = state.settings.spellBlockIncludeAudio;
        }
        renderEffectPatchUi();

        // 5. 泥人配置回显
        if (state.settings.mudManTier !== undefined && state.settings.mudManTier !== null) {
          selectedMudManTier = state.settings.mudManTier;
        }
        if (state.mudMan !== undefined) {
          mudManState = state.mudMan;
        }
        renderMudManUi();

        // 6. 头顶血条状态回显
        if (state.healthBar !== undefined) {
          healthBarState = state.healthBar;
        }
        renderHealthBarUi();

        // 7. PoE 2 商城外观 (MTX) 回显
        if (payload.mtx !== undefined) {
          mtxState = payload.mtx;
        }

        // 同步 6 个模块卡片的真实状态。
        //
        // 这些 span 原本是硬编码文案（如「● 14 条规则生效中」「● 5 种神图光柱激活」），
        // 无论用户是否打过补丁都恒显示「生效中」，属虚假状态。
        // 现在改为按设置与后端数据实时计算。
        syncModuleHubStatus();
        if (state.settings) {
          if (state.settings.mtxSelectedNames) {
            selectedMtxNames = new Set(state.settings.mtxSelectedNames);
          }
          if (state.settings.mtxSelectedArmourSet !== undefined) {
            selectedArmourSet = state.settings.mtxSelectedArmourSet;
          }
          if (state.settings.mtxSelectedPortal !== undefined) {
            selectedPortal = state.settings.mtxSelectedPortal;
          }
          if (state.settings.mtxSelectedPet !== undefined) {
            selectedPet = state.settings.mtxSelectedPet;
          }
          if (state.settings.mtxSelectedClass) {
            selectedMtxClass = state.settings.mtxSelectedClass;
            const selCls = element("selMtxCharacterClass");
            if (selCls) selCls.value = selectedMtxClass;
          }
        }
        renderMtxUi();
      }
    },

    receiveMtxDatabase(payload) {
      if (!payload || !Array.isArray(payload.entries)) return;
      allMtxEntries = payload.entries;
      renderMtxUi();
    }
  };
})();
