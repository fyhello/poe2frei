(() => {
  let state = null;
  const element = id => document.getElementById(id);
  const post = (type, value = {}) => window.chrome?.webview?.postMessage({ version: 1, type, payload: value });

  // 6 大模块标题字典
  const MODULE_TITLES = {
    colormod: "🎨 词缀多色彩染色 (ColorMod)",
    mapbeam: "🌟 神图冲天光柱 (MapBeam)",
    chests: "📦 宝箱掉落标注与透视 (Chests)",
    rumours: "🔮 异界地图传言标注 (Rumours)",
    fontmod: "🔤 游戏字体与排版 (FontMod)",
    price: "💰 原生价格深度注入 (Price)"
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

  // 前端工作态（以 C# 后端真实扫描结果与持久化配置为唯一真实数据源）
  let colorModData = [];
  let officialDefaultSnapshot = []; // 官方默认推荐底包（不可篡改、兜底恢复）
  let customPresets = {}; // 用户自定义预设字典: { [id]: { id, name, data, createdAt } }
  let currentPresetId = "default";
  const PRESET_STORAGE_KEY = "frei_colormod_custom_presets";

  let lastSnapshot = [];
  let isClientSynced = false;
  let currentGroup = "地图";
  let previewType = "map";
  let colorModCurrentPage = 1;
  const COLOR_MOD_PAGE_SIZE = 100;

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
    if (newId === "default") {
      if (officialDefaultSnapshot && officialDefaultSnapshot.length > 0) {
        colorModData = JSON.parse(JSON.stringify(officialDefaultSnapshot));
        showToast("已恢复为【🌟 默认预设 (官方推荐)】");
      } else {
        showToast("⚠️ 暂未获取到官方默认预设，请点击【读取客户端】以提取");
      }
    } else if (customPresets[newId]) {
      colorModData = JSON.parse(JSON.stringify(customPresets[newId].data));
      showToast(`已加载自定义预设【${customPresets[newId].name}】`);
    }
    colorModCurrentPage = 1;
    renderTable();
    renderLiveCard();
    updatePresetActionButtons();
  }

  function saveCustomPreset(name) {
    const trimmed = name?.trim();
    if (!trimmed) return "预设名称不能为空";
    if (trimmed === "默认预设" || trimmed.includes("官方推荐") || trimmed.toLowerCase() === "default") {
      return "默认预设为官方基础兜底方案，禁止覆盖！请使用其他名称保存为自定义预设。";
    }
    const presetId = "preset_" + Date.now();
    customPresets[presetId] = {
      id: presetId,
      name: trimmed,
      data: JSON.parse(JSON.stringify(colorModData)),
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
      if (officialDefaultSnapshot && officialDefaultSnapshot.length > 0) {
        colorModData = JSON.parse(JSON.stringify(officialDefaultSnapshot));
      }
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
    renderTable();
    renderLiveCard();
    showToast(checked ? `已全选当前筛选的 ${list.length} 条词缀` : `已取消勾选当前筛选的 ${list.length} 条词缀`);
  }

  // 模块总开关
  const moduleMasters = {
    colormod: true,
    mapbeam: true,
    chests: true,
    rumours: true,
    fontmod: true,
    price: true
  };

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

    const btnSave = element("btnApplyCurrentWorkbench") || element("btnApplyAllWorkbench");
    if (btnSave) {
      btnSave.textContent = "💾 保存并同步当前功能";
      btnSave.title = `仅保存并同步【${MODULE_TITLES[moduleId] || "当前功能"}】配置到游戏`;
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

  function updateCardPreviewInfo() {
    const title = element('cardTitle');
    const sub = element('cardSubtitle');
    const props = element('cardBaseProps');
    const note = element('cardBottomNote');
    const loot = element('lootLabelAffix');

    if (previewType === 'gear') {
      if (title) title.innerText = "法师之血 重革腰带";
      if (sub) sub.innerText = "传奇 重革腰带 (Mageblood)";
      if (props) props.innerHTML = `<span>需求等级: <strong>44</strong></span><span>品质: <strong style="color: #60a5fa;">+20%</strong></span>`;
      if (note) note.innerText = "右键点击以装备到腰带插槽";
      if (loot) loot.innerHTML = `<span style="color: #d4a754; font-weight: bold;">法师之血 [传奇]</span>`;
    } else if (previewType === 'tablet') {
      if (title) title.innerText = "远征日耀石板 (Sun Tablet)";
      if (sub) sub.innerText = "远征先祖石板";
      if (props) props.innerHTML = `<span>物品掉落数量: <strong>+15%</strong></span><span>遗迹数量: <strong>+2</strong></span>`;
      if (note) note.innerText = "放置在远征大地图仪中以激活效果";
      if (loot) loot.innerHTML = `<span style="color: #fbbf24; font-weight: bold;">远征日耀石板 [先祖]</span>`;
    } else if (previewType === 'endgame') {
      if (title) title.innerText = "终局征服者圣殿";
      if (sub) sub.innerText = "阶级: 16 终局首领区域";
      if (props) props.innerHTML = `<span>首领专属掉落: <strong>+100%</strong></span><span>附带专属侍从: <strong>4</strong></span>`;
      if (note) note.innerText = "在终局时空仪中使用以开启裂隙门";
      if (loot) loot.innerHTML = `<span style="color: #f43f5e; font-weight: bold;">终局征服者圣殿 [T16]</span>`;
    } else {
      if (title) title.innerText = "阿兹莫里山区 (Azmerian Ranges)";
      if (sub) sub.innerText = "阶级: 15 异界地图";
      if (props) props.innerHTML = `<span>物品稀有度: <strong style="color: var(--gold-primary, #d4a754);">稀有 (Rare)</strong></span><span>物品数量: <strong>+68%</strong></span>`;
      if (note) note.innerText = "在制图仪中使用以开启传送门";
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
          renderTable();
          renderLiveCard(); 
        }
      });
    });
    tbody.querySelectorAll(".btn-del-rule").forEach(btn => {
      btn.addEventListener("click", e => {
        colorModData = colorModData.filter(x => x.id !== e.target.dataset.id);
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

  function initChestsFromDefault(defaultChests) {
    if (CHESTS_DATA.length > 0) return;
    if (!defaultChests) return;
    CHESTS_DATA = Object.entries(defaultChests).map(([orig, mod]) => ({
      originalName: orig,
      modifiedName: mod,
      desc: "预设规则匹配",
      color: mod.includes("rgb") ? "#a855f7" : "#eab308",
      enabled: true,
      category: "预设",
      groupedRowIndices: []
    }));
    updateChestsBadge(false, `预设 (${CHESTS_DATA.length}类)`);
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
    showToast("正在将【词缀多色彩染色】配置写入 PoE2 游戏客户端，请稍候...");
    post('applyColorModPatch', {
      enabled: moduleMasters.colormod,
      style: element('patcherColorModStyle')?.value || 'Whole',
      rules: colorModData.map(r => ({
        id: r.id,
        text: r.text || r.displayName || r.id,
        group: r.group || '通用',
        color: r.color || '#ffffff',
        style: r.style || 'Whole',
        tag: r.tag || '',
        isDanger: !!r.isDanger,
        enabled: !!r.enabled
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

  function applyPrice() {
    if (pendingPricePreviewToken) {
      const token = pendingPricePreviewToken;
      hidePricePreview();
      showToast("正在将已确认物价写入客户端...");
      post('confirmCurrentPricesPatch', { token });
      const btnSave = element("btnApplyCurrentWorkbench") || element("btnApplyAllWorkbench");
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
    const btnSave = element("btnApplyCurrentWorkbench") || element("btnApplyAllWorkbench");
    if (btnSave && currentActiveModule === "price") {
      btnSave.textContent = "🚀 确认并写入客户端";
      btnSave.title = "预览已完成，点击立即将已确认物价写入客户端游戏文件";
      btnSave.style.background = "#059669";
    }

    const searchInput = element('pricePreviewSearch');
    if (searchInput) searchInput.value = "";
    renderPricePreviewTable("");
  }

  function confirmPrice() {
    if (!pendingPricePreviewToken) {
      showToast("请先预览客户端匹配结果。");
      return;
    }
    const token = pendingPricePreviewToken;
    hidePricePreview();
    showToast("正在确认注入客户端物价...");
    post('confirmCurrentPricesPatch', { token });
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
      default:
        console.warn("未知当前功能模块:", currentActiveModule);
        break;
    }
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

    const matches = CLIENT_STAT_DICTIONARY.filter(item => item.text.toLowerCase().includes(q)).slice(0, 8);
    if (matches.length === 0) {
      dropdown.classList.add("hidden");
      return;
    }

    dropdown.innerHTML = "";
    matches.forEach(m => {
      const opt = document.createElement("div");
      opt.className = "autocomplete-item";
      opt.innerHTML = `
        <div style="font-size: 11px; color: #fff;">${m.text}</div>
        <div style="display: flex; gap: 6px; font-size: 10px; color: var(--text-muted, #94a3b8); margin-top: 2px;">
          <span>分组: ${m.group}</span>
          ${m.tag ? `<span style="color: ${m.color || '#d4a754'}">#${m.tag}</span>` : ''}
        </div>
      `;
      opt.addEventListener("click", () => {
        element("modalRuleText").value = m.text;
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
    if (!box) return;

    if (style === "NumberOnly") {
      const rendered = text.replace(/((\+|-)?#%?|(\+|-)?\d+(?:\.\d+)?%?|\(\d+[-~]\d+\)%?|\(#[-~]#\)%?|\{\d+\}%?)/g, `<span style="color: ${color}; font-weight: bold;">$1</span>`);
      box.innerHTML = `预览：${rendered}`;
    } else {
      box.innerHTML = `预览：<span style="color: ${color}; font-weight: 500;">${text}</span>`;
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
    if (!bytes || bytes <= 0) return "115.4 MB";
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
        html += `
          <div class="snapshot-card official">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${name}</span>
                <span class="snapshot-tag official">官方出厂底包</span>
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 拍摄于: ${formatDate(created)}</span>
                <span>📌 锁定状态: 禁止删除/覆盖</span>
              </div>
              <div style="font-size: 11px; color: var(--text-muted); margin-top: 2px;">
                ${desc}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${name}" title="将客户端恢复为此版本，所有补丁重置为关闭状态">
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

        html += `
          <div class="snapshot-card">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${name}</span>
                <span class="snapshot-tag manual">手动备份</span>
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 创建时间: ${formatDate(created)}</span>
                <span>${desc}</span>
              </div>
              <div class="snapshot-modules-row">
                ${modulesHtml}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${name}">
                ↺ 恢复此版本
              </button>
              <button class="btn-delete-snap" data-snap-id="${id}" data-snap-name="${name}">
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

        html += `
          <div class="snapshot-card">
            <div class="snapshot-info-col">
              <div class="snapshot-title-row">
                <span class="snapshot-name">${name}</span>
                <span class="snapshot-tag auto">自动快照</span>
                <span style="font-size: 11px; color: var(--muted);">${formatSize(size)}</span>
              </div>
              <div class="snapshot-desc-row">
                <span>🕒 记录时间: ${formatDate(created)}</span>
                <span>${desc}</span>
              </div>
              <div class="snapshot-modules-row">
                ${modulesHtml}
              </div>
            </div>
            <div class="snapshot-actions">
              <button class="btn-restore-snap" data-snap-id="${id}" data-snap-name="${name}">
                ↺ 恢复此版本
              </button>
              <button class="btn-delete-snap" data-snap-id="${id}" data-snap-name="${name}">
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
  }

  window.patcherUi = {
    initialize() {
      // 1. 卡片点击进入工作台
      document.querySelectorAll(".hub-card").forEach(card => {
        card.addEventListener("click", () => {
          const mod = card.dataset.module;
          if (mod) openWorkbench(mod);
        });
      });

      // 2. 卡片独立开关
      Object.keys(moduleMasters).forEach(mod => {
        const toggle = element("masterToggle-" + mod);
        toggle?.addEventListener("click", e => e.stopPropagation());
        toggle?.addEventListener("change", e => toggleModuleMaster(mod, e.target.checked, e));
      });

      // 3. 工作台返回与直达下拉
      element("btnBackToHub")?.addEventListener("click", backToHub);
      element("wbModuleSelect")?.addEventListener("change", e => openWorkbench(e.target.value));

      // 4. 路径更新
      element('btnSetGameDir')?.addEventListener('click', () => {
        const path = element('patcherGameDir')?.value?.trim();
        post('setPatcherGameDir', { path });
      });

      // 5. 快照版本管理与还原纯净客户端
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
      element('btnRestorePureTop')?.addEventListener('click', handleRestore);

      // 6. 保存与同步（仅保存同步当前明细模块）
      const saveBtn = element('btnApplyCurrentWorkbench') || element('btnApplyAllWorkbench');
      saveBtn?.addEventListener('click', saveAndApplyCurrentModule);

      // 7. 预设与真实词库扫描
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

      // 8. 词缀染色过滤与右侧仿真视窗联动
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
        renderTable();
        showToast("已批量设为整句着色");
      });
      element('btnBatchNumber')?.addEventListener('click', () => {
        colorModData.forEach(i => { if (i.group === currentGroup) i.style = 'NumberOnly'; });
        renderTable();
        showToast("已批量设为仅数字着色");
      });
      element('cardTypeSelect')?.addEventListener('change', e => {
        previewType = e.target.value;
        renderLiveCard();
      });

      // 10. 字体预设、细分字号与加粗交互
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

      element('inputLocalFont')?.addEventListener('change', e => {
        const file = e.target.files?.[0];
        if (!file) return;
        const fontName = file.name.replace(/\.[^/.]+$/, "");
        const reader = new FileReader();
        reader.onload = evt => {
          try {
            const fontFace = new FontFace(fontName, evt.target.result);
            fontFace.load().then(loadedFace => {
              document.fonts.add(loadedFace);
              const dropdown = element('fontSelectDropdown');
              if (dropdown) {
                let opt = dropdown.querySelector(`option[value="${fontName}"]`);
                if (!opt) {
                  opt = document.createElement('option');
                  opt.value = fontName;
                  opt.text = `📁 ${fontName} (自定义本地字体)`;
                  dropdown.insertBefore(opt, dropdown.firstChild);
                }
                dropdown.value = fontName;
              }
              updateFontPreview();
              showToast(`已载入并选用本地字体：${fontName}`);
            }).catch(err => {
              showToast(`载入字体失败: ${err.message}`, true);
            });
          } catch (err) {
            showToast(`解析字体文件失败: ${err.message}`, true);
          }
        };
        reader.readAsArrayBuffer(file);
      });

      // 11. 价格设置
      element('priceSepSelect')?.addEventListener('change', updatePriceSim);
      element('btnPreviewPricePatch')?.addEventListener('click', previewPrice);

      element('btnPriceSelectAll')?.addEventListener('click', () => {
        Object.keys(PRICE_CATEGORY_INPUTS).forEach(id => {
          const el = element(id);
          if (el) el.checked = true;
        });
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
          updatePriceSim();
          if (currentPreviewItems && currentPreviewItems.length > 0) {
            renderPricePreviewTable(element('pricePreviewSearch')?.value || '');
          }
        });
      });

      // 12. 传言搜索
      element('rumourSearch')?.addEventListener('input', renderRumoursTable);

      // 13. 模态框打开与关闭
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
          navigator.clipboard?.writeText(val);
          showToast("已复制配置码到剪贴板！");
          element('ioModal')?.classList.add('hidden');
        }
      });

      // 初始化所有表格与视图
      loadCustomPresets();
      renderTable();
      renderMapBeam();
      renderChestsList();
      renderRumoursTable();
      updatePriceSim();
    },

    receive(payload) {
      if (!payload) return;
      state = payload;
      renderStatus();
      if (payload.pricePreview) {
        renderPricePreview(payload.pricePreview);
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

        // 真实挂载到主表格：合并已有配置项，避免冲掉用户已勾选或修改的颜色
        const existingMap = new Map();
        colorModData.forEach(r => existingMap.set(r.id, r));

        colorModData = payload.scannedEntries.map((entry, idx) => {
          const statId = entry.statId || ("stat_" + idx);
          const rawText = entry.displayName || entry.text || statId;
          if (existingMap.has(statId)) {
            const ex = existingMap.get(statId);
            ex.text = rawText;
            ex.displayName = rawText;
            return ex;
          }
          const isDanger = !!entry.isDanger;
          const col = entry.color || (isDanger ? "#E3170D" : "#ffffff");
          return {
            id: statId,
            text: rawText,
            displayName: rawText,
            group: entry.group || "通用",
            color: col,
            style: entry.style || "Whole",
            tag: isDanger ? "高危词缀" : (entry.currentColorTag || (entry.group + "词缀")),
            isDanger: isDanger,
            enabled: !!entry.enabled
          };
        });
        colorModCurrentPage = 1;
        if (officialDefaultSnapshot.length === 0 && colorModData.length > 0) {
          officialDefaultSnapshot = JSON.parse(JSON.stringify(colorModData));
        }
        renderTable();
        renderLiveCard();
      }

      // 2. 持久化配置回显
      if (state.settings) {
        if (state.settings.colorModEnabled !== undefined) toggleModuleMaster("colormod", state.settings.colorModEnabled);
        if (state.settings.mapBeamEnabled !== undefined) toggleModuleMaster("mapbeam", state.settings.mapBeamEnabled);
        if (state.settings.chestEnhanced !== undefined) toggleModuleMaster("chests", state.settings.chestEnhanced);
        if (state.settings.rumourEnhanced !== undefined) toggleModuleMaster("rumours", state.settings.rumourEnhanced);
        if (state.settings.pricePatchEnabled !== undefined) toggleModuleMaster("price", state.settings.pricePatchEnabled);
        applyPriceSettings(state.settings);

        // 1. 初始化纯净官方推荐底包 (仅由 initialColorMods 提供，绝不受用户已保存设置污染)
        if (officialDefaultSnapshot.length === 0 && Array.isArray(payload.initialColorMods) && payload.initialColorMods.length > 0) {
          officialDefaultSnapshot = payload.initialColorMods.map((entry, idx) => {
            const statId = entry.statId || ("init_" + idx);
            const rawText = entry.displayName || entry.text || statId;
            const isDanger = !!entry.isDanger;
            const col = entry.color || (isDanger ? "#E3170D" : "#ffffff");
            return {
              id: statId,
              text: rawText,
              displayName: rawText,
              group: entry.group || "通用",
              color: col,
              style: entry.style || "Whole",
              tag: isDanger ? "高危词缀" : (entry.currentColorTag || (entry.group + "词缀")),
              isDanger: isDanger,
              enabled: !!entry.enabled
            };
          });
        }

        // 2. 回显词缀染色规则 (用户保存的自定义配置)
        if (Array.isArray(state.settings.colorModRules) && state.settings.colorModRules.length > 0) {
          colorModData = state.settings.colorModRules.map(r => ({
            id: r.id,
            text: r.text || r.displayName || r.id,
            displayName: r.text || r.displayName || r.id,
            group: r.group || "通用",
            color: r.color || "#ffffff",
            style: r.style || "Whole",
            tag: r.tag || (r.isDanger ? "高危词缀" : ""),
            isDanger: !!r.isDanger,
            enabled: !!r.enabled
          }));
          colorModCurrentPage = 1;
          // 同步初始化上次生效快照，保证点击【引用上次】立即生效可用
          lastSnapshot = JSON.parse(JSON.stringify(colorModData));
          renderTable();
          renderLiveCard();
        } else if (officialDefaultSnapshot.length > 0 && colorModData.length === 0) {
          colorModData = JSON.parse(JSON.stringify(officialDefaultSnapshot));
          colorModCurrentPage = 1;
          lastSnapshot = JSON.parse(JSON.stringify(colorModData));
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
        }

        // 3. 宝箱真实数据装配与回显
        if (Array.isArray(payload.clientChests) && payload.clientChests.length > 0) {
          element("scanChestsSpinner")?.classList.add("hidden");
          initChestsFromClient(payload.clientChests);
        } else if (CHESTS_DATA.length === 0 && payload.defaultChests) {
          initChestsFromDefault(payload.defaultChests);
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
      }
    }
  };
})();
