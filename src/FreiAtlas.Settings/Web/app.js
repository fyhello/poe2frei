const protocolVersion = 1;
const snapshotStaleAfterMs = 5000;
const nodeCategories = [
  { id: 'Completed', label: '已完成', className: 'completed' },
  { id: 'Unlocked', label: '已解锁', className: 'unlocked' },
  { id: 'Locked', label: '未解锁', className: 'locked' }
];

let runtime = null;
let draftSettings = null;
let catalog = [];
let activePage = 'nodes';
let navigationSearch = '';
let lastSnapshotAt = 0;
let hotkeyRecording = false;
let localHotkeyError = '';

const $ = id => document.getElementById(id);
const clone = value => JSON.parse(JSON.stringify(value));

function stableSettings(value) {
  if (value == null) return value;
  const stable = clone(value);
  if (stable.navigation?.rules) {
    stable.navigation.rules = Object.fromEntries(
      Object.entries(stable.navigation.rules)
        .sort(([left], [right]) => left.localeCompare(right)));
  }
  return stable;
}

const settingsEqual = (left, right) =>
  JSON.stringify(stableSettings(left)) === JSON.stringify(stableSettings(right));

function editableSettings() {
  if (!runtime?.settings) return null;
  return clone(draftSettings ?? runtime.settings);
}

function displayedSettings() {
  return draftSettings ?? runtime?.settings ?? null;
}

function renderAltOverlayMode() {
  const mode = displayedSettings()?.altOverlayMode || 'HoldToHide';
  [
    ['altModeHold', 'HoldToHide'],
    ['altModeToggle', 'ToggleOnPress']
  ].forEach(([id, value]) => {
    const button = $(id);
    const selected = mode === value;
    button.classList.toggle('selected', selected);
    button.setAttribute('aria-pressed', String(selected));
  });
}

function updateAltOverlayMode(mode) {
  const settings = editableSettings();
  if (!settings || settings.altOverlayMode === mode) return;
  settings.altOverlayMode = mode;
  submitSettings(settings);
}

function post(type, payload = {}) {
  const envelope = { version: protocolVersion, type, payload };
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(envelope);
  }
}

function setToggle(button, state) {
  const mixed = state === 'mixed';
  const enabled = state === true;
  button.classList.toggle('on', enabled);
  button.classList.toggle('mixed', mixed);
  button.setAttribute('aria-checked', state === 'mixed' ? 'mixed' : String(enabled));
}

function aggregateToggle(values) {
  if (values.every(Boolean)) return true;
  if (values.every(value => !value)) return false;
  return 'mixed';
}

function renderIcons() {
  if (window.lucide) window.lucide.createIcons();
}

function stateLabel(state) {
  return {
    InGame: '游戏中',
    CharacterNotLoaded: '角色未载入',
    ReadFailed: '读取失败',
    Exited: '进程已退出'
  }[state] || state || '未知';
}

function stateClass(state) {
  if (state === 'InGame') return '';
  if (state === 'CharacterNotLoaded') return 'warning';
  if (state === 'ReadFailed' || state === 'Exited') return 'danger';
  return 'muted';
}

function renderProcessBar() {
  const processes = runtime?.processes || [];
  const selected = processes.find(item => item.processId === runtime?.selectedProcessId);
  const summary = $('selectedProcess');
  const dot = $('processDot');
  if (selected) {
    summary.textContent = `${selected.characterName || stateLabel(selected.state)}　PID ${selected.processId}　${stateLabel(selected.state)}`;
    dot.className = `status-dot ${stateClass(selected.state)}`;
  } else if (processes.length) {
    summary.textContent = '请选择游戏进程';
    dot.className = 'status-dot warning';
  } else {
    summary.textContent = '未检测到游戏进程';
    dot.className = 'status-dot muted';
  }

  const menu = $('processMenu');
  menu.replaceChildren();
  if (!processes.length) {
    const empty = document.createElement('div');
    empty.className = 'process-empty';
    empty.textContent = '未检测到游戏进程';
    menu.append(empty);
  } else {
    processes.forEach(process => {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = `process-row${process.processId === runtime.selectedProcessId ? ' selected' : ''}`;
      row.disabled = Boolean(runtime.isOverlayRunning) || Boolean(runtime.isOverlayStopping) || process.state === 'Exited';
      row.innerHTML = `<span class="character"><strong></strong><small>PID ${process.processId}</small></span><span class="process-state"><span class="status-dot ${stateClass(process.state)}"></span>${stateLabel(process.state)}</span>`;
      row.querySelector('strong').textContent = process.characterName || stateLabel(process.state);
      row.addEventListener('click', () => {
        post('selectProcess', { processId: process.processId });
        closeProcessMenu();
      });
      menu.append(row);
    });
  }

  const overlayButton = $('overlayButton');
  const running = Boolean(runtime?.isOverlayRunning);
  const stopping = Boolean(runtime?.isOverlayStopping);
  $('refreshButton').disabled = false;
  overlayButton.classList.toggle('stop', running);
  overlayButton.disabled = stopping || (!running && !runtime?.selectedProcessId);
  overlayButton.innerHTML = stopping
    ? '<i data-lucide="loader-circle"></i><span>正在停止</span>'
    : running
      ? '<i data-lucide="square"></i><span>停止地图覆盖层</span>'
      : '<i data-lucide="play"></i><span>启动地图覆盖层</span>';
  $('processSelect').disabled = running || stopping;
  const overlayStatus = $('overlayStatus');
  const currentStatus = runtime?.overlayStatusMessage || (running ? '覆盖层运行中' : '覆盖层未启动');
  overlayStatus.dataset.currentStatus = currentStatus;
  overlayStatus.textContent = currentStatus;
  updateStaleStatus();
}

function renderHotkey() {
  const button = $('hotkeyButton');
  const value = $('hotkeyValue');
  button.disabled = !runtime;
  button.classList.toggle('recording', hotkeyRecording);
  button.setAttribute('aria-pressed', String(hotkeyRecording));
  value.textContent = hotkeyRecording
    ? '请按键'
    : runtime?.overlayToggleHotkey?.gesture || 'F12';
  $('hotkeyError').textContent = localHotkeyError
    || runtime?.hotkeyRegistrationError
    || '';
}

function hotkeyGestureFromEvent(event) {
  if (event.metaKey) return { error: '不支持 Windows 键' };
  if (['Control', 'Alt', 'Shift', 'Meta'].includes(event.key)) return null;
  const modifiers = [];
  if (event.ctrlKey) modifiers.push('Ctrl');
  if (event.altKey) modifiers.push('Alt');
  if (event.shiftKey) modifiers.push('Shift');
  const upperKey = event.code.startsWith('Key')
    ? event.code.slice(3).toUpperCase()
    : event.code.startsWith('Digit')
      ? event.code.slice(5)
      : event.key.toUpperCase();
  if (/^F([1-9]|1[0-2])$/.test(upperKey)) {
    return modifiers.length
      ? { error: 'F1-F12 请单独使用' }
      : { gesture: upperKey };
  }

  if (/^[A-Z0-9]$/.test(upperKey)) {
    return modifiers.length
      ? { gesture: [...modifiers, upperKey].join('+') }
      : { error: '字母或数字需要组合 Ctrl、Alt 或 Shift' };
  }

  return { error: '仅支持 F1-F12 或修饰键加字母、数字' };
}

function handleHotkeyRecorder(event) {
  if (!hotkeyRecording) return;
  event.preventDefault();
  event.stopPropagation();
  if (event.key === 'Escape') {
    hotkeyRecording = false;
    localHotkeyError = '';
    renderHotkey();
    return;
  }

  const result = hotkeyGestureFromEvent(event);
  if (!result) return;
  if (result.error) {
    localHotkeyError = result.error;
    renderHotkey();
    return;
  }

  const gesture = result.gesture;
  hotkeyRecording = false;
  localHotkeyError = '';
  renderHotkey();
  post('updateHotkey', { gesture });
}

function renderNodeRows() {
  const container = $('nodeRows');
  container.replaceChildren();
  const settings = displayedSettings();
  if (!settings?.nodes) return;
  const allNames = aggregateToggle(nodeCategories.map(category => settings.nodes[category.id].showMapNames));
  const allConnections = aggregateToggle(nodeCategories.map(category => settings.nodes[category.id].showConnections));
  const allContents = aggregateToggle(nodeCategories.map(category => settings.nodes[category.id].showMapContents));
  appendNodeRow(container, { id: 'All', label: '所有', className: '' }, allNames, allConnections, allContents, true);
  nodeCategories.forEach(category => {
    const value = settings.nodes[category.id];
    appendNodeRow(container, category, value.showMapNames, value.showConnections, value.showMapContents, false);
  });
  $('nodeCount').textContent = `${runtime.nodeCounts?.total || 0} 个节点`;
}

function appendNodeRow(container, category, showNames, showConnections, showContents, master) {
  const toggleRole = master ? 'checkbox' : 'switch';
  const row = document.createElement('div');
  row.className = `setting-row${master ? ' master' : ''}`;
  const name = document.createElement('div');
  name.className = 'category-name';
  name.innerHTML = category.className ? `<span class="category-swatch ${category.className}"></span><span>${category.label}</span>` : `<span>${category.label}</span>`;
  const nameCell = document.createElement('div');
  nameCell.className = 'center';
  const nameToggle = document.createElement('button');
  nameToggle.type = 'button';
  nameToggle.className = 'toggle';
  nameToggle.setAttribute('role', toggleRole);
  nameToggle.setAttribute('aria-label', `${category.label}地图名称`);
  setToggle(nameToggle, showNames);
  nameToggle.addEventListener('click', () => updateNodeSetting(category.id, 'showMapNames', showNames !== true));
  nameCell.append(nameToggle);
  const connectionCell = document.createElement('div');
  connectionCell.className = 'center';
  const connectionToggle = document.createElement('button');
  connectionToggle.type = 'button';
  connectionToggle.className = 'toggle';
  connectionToggle.setAttribute('role', toggleRole);
  connectionToggle.setAttribute('aria-label', `${category.label}节点连线`);
  setToggle(connectionToggle, showConnections);
  connectionToggle.addEventListener('click', () => updateNodeSetting(category.id, 'showConnections', showConnections !== true));
  connectionCell.append(connectionToggle);
  const contentCell = document.createElement('div');
  contentCell.className = 'center';
  const contentToggle = document.createElement('button');
  contentToggle.type = 'button';
  contentToggle.className = 'toggle';
  contentToggle.setAttribute('role', toggleRole);
  contentToggle.setAttribute('aria-label', `${category.label}地图内容`);
  setToggle(contentToggle, showContents);
  contentToggle.addEventListener('click', () => updateNodeSetting(category.id, 'showMapContents', showContents !== true));
  contentCell.append(contentToggle);
  row.append(name, nameCell, connectionCell, contentCell);
  container.append(row);
}

function updateNodeSetting(categoryId, key, enabled) {
  const settings = editableSettings();
  if (!settings) return;
  const targets = categoryId === 'All' ? nodeCategories.map(item => item.id) : [categoryId];
  targets.forEach(id => settings.nodes[id][key] = enabled);
  submitSettings(settings);
}

function renderContents() {
  const list = $('contentList');
  list.replaceChildren();
  const settings = displayedSettings();
  if (!settings) return;
  const query = $('contentSearch').value.trim().toLocaleLowerCase('zh-CN');
  let visibleCount = 0;
  catalog.forEach(entry => {
    const shown = settings.contentVisibility[entry.id] !== false;
    if (shown) visibleCount++;
    const item = document.createElement('div');
    item.className = 'content-item';
    if (query && !entry.displayName.toLocaleLowerCase('zh-CN').includes(query) && !entry.id.toLowerCase().includes(query)) item.classList.add('hidden');
    const image = document.createElement('img');
    image.src = entry.iconPath;
    image.alt = '';
    const label = document.createElement('span');
    label.className = 'content-name';
    label.textContent = entry.displayName;
    label.title = entry.displayName;
    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'toggle';
    setToggle(toggle, shown);
    toggle.addEventListener('click', () => {
      const settings = editableSettings();
      if (!settings) return;
      settings.contentVisibility[entry.id] = !shown;
      submitSettings(settings);
    });
    item.append(image, label, toggle);
    list.append(item);
  });
  $('contentCount').textContent = `${visibleCount} / ${catalog.length} 已显示`;
}

function setAllContent(enabled) {
  if (!runtime?.settings) return;
  const settings = editableSettings();
  if (!settings) return;
  catalog.forEach(entry => settings.contentVisibility[entry.id] = enabled);
  submitSettings(settings);
}

function navigationNameKey(value) {
  return value.trim().toLocaleLowerCase('zh-CN');
}

function mergeNavigationEntries(settings) {
  const entries = new Map();
  const hideCompletedMaps = settings.navigation.hideCompletedMaps === true;
  (runtime?.navigationCatalog?.entries || []).forEach(entry => {
    const displayName = entry.displayName.trim();
    if (!displayName) return;
    const incompleteNodeCount = entry.incompleteNodeCount ?? entry.nodeCount;
    entries.set(navigationNameKey(displayName), {
      displayName,
      nodeCount: hideCompletedMaps ? incompleteNodeCount : entry.nodeCount,
      hiddenCompleted: hideCompletedMaps && incompleteNodeCount === 0,
      isCurrent: true,
      ruleName: displayName,
      rule: null
    });
  });

  Object.entries(settings.navigation.rules || {}).forEach(([ruleName, rule]) => {
    const displayName = ruleName.trim();
    if (!displayName) return;
    const key = navigationNameKey(displayName);
    const entry = entries.get(key);
    if (entry) {
      entry.ruleName = ruleName;
      entry.rule = rule;
    } else {
      entries.set(key, {
        displayName,
        nodeCount: null,
        hiddenCompleted: false,
        isCurrent: false,
        ruleName,
        rule
      });
    }
  });

  return [...entries.values()]
    .filter(entry => !entry.hiddenCompleted)
    .map(entry => ({
      ...entry,
      active: Boolean(entry.rule?.highlight || entry.rule?.route || entry.rule?.direction)
    }))
    .sort((left, right) => {
      if (left.active !== right.active) return left.active ? -1 : 1;
      return left.displayName.localeCompare(right.displayName, 'zh-CN', { sensitivity: 'base' })
        || left.displayName.localeCompare(right.displayName, 'zh-CN');
    });
}

function updateNavigationMode(targetMode) {
  const settings = editableSettings();
  if (!settings?.navigation || settings.navigation.targetMode === targetMode) return;
  settings.navigation.targetMode = targetMode;
  submitSettings(settings);
}

function updateNavigationRule(entry, property, enabled) {
  const settings = editableSettings();
  if (!settings?.navigation) return;
  const current = settings.navigation.rules[entry.ruleName]
    || { highlight: false, route: false, direction: false };
  const rule = { ...current, [property]: enabled };
  if (!rule.highlight && !rule.route && !rule.direction) {
    delete settings.navigation.rules[entry.ruleName];
  } else {
    settings.navigation.rules[entry.ruleName] = rule;
  }
  submitSettings(settings);
}

function appendNavigationCheckbox(row, entry, property, label) {
  const cell = document.createElement('label');
  cell.className = 'navigation-check-cell';
  const input = document.createElement('input');
  input.type = 'checkbox';
  input.className = 'navigation-check';
  input.checked = entry.rule?.[property] === true;
  input.setAttribute('aria-label', `${entry.displayName} ${label}`);
  input.addEventListener('change', () => updateNavigationRule(entry, property, input.checked));
  cell.append(input);
  row.append(cell);
}

function renderNavigation() {
  const navigationCatalog = runtime?.navigationCatalog;
  const directoryEntries = navigationCatalog?.entries || [];
  const rows = $('navigationRows');
  rows.replaceChildren();
  const settings = displayedSettings();
  if (!settings?.navigation) return;

  const hideCompletedMaps = settings.navigation.hideCompletedMaps === true;
  const visibleDirectoryEntries = hideCompletedMaps
    ? directoryEntries.filter(entry => (entry.incompleteNodeCount ?? entry.nodeCount) > 0)
    : directoryEntries;
  const directoryNodeCount = visibleDirectoryEntries.reduce(
    (total, entry) => total + (hideCompletedMaps
      ? (entry.incompleteNodeCount ?? entry.nodeCount)
      : entry.nodeCount),
    0);
  const status = navigationCatalog?.status || 'Waiting';
  $('navigationStatus').textContent = status === 'Waiting'
    ? '正在等待地图目录'
    : `${visibleDirectoryEntries.length} 个地图 · ${directoryNodeCount} 个${hideCompletedMaps ? '未完成' : ''}节点 · ${status === 'Live' ? '实时读取' : '上次读取'}`;

  const nearest = settings.navigation.targetMode === 'Nearest';
  $('navigationModeNearest').classList.toggle('selected', nearest);
  $('navigationModeNearest').setAttribute('aria-pressed', String(nearest));
  $('navigationModeAll').classList.toggle('selected', !nearest);
  $('navigationModeAll').setAttribute('aria-pressed', String(!nearest));
  setToggle($('navigationHideCompletedToggle'), hideCompletedMaps);
  $('clearNavigationRules').disabled = Object.keys(settings.navigation.rules || {}).length === 0;

  const query = navigationSearch.trim().toLocaleLowerCase('zh-CN');
  const entries = mergeNavigationEntries(settings)
    .filter(entry => !query || navigationNameKey(entry.displayName).includes(query));
  if (!entries.length) {
    const empty = document.createElement('div');
    empty.className = 'navigation-empty';
    empty.textContent = query ? '没有匹配的地图' : '目录中暂无地图';
    rows.append(empty);
    return;
  }

  entries.forEach(entry => {
    const row = document.createElement('div');
    row.className = 'navigation-row';
    row.setAttribute('role', 'row');
    const nameCell = document.createElement('div');
    nameCell.className = 'navigation-name-cell';
    const name = document.createElement('span');
    name.className = 'navigation-name';
    name.textContent = entry.displayName;
    name.title = entry.displayName;
    nameCell.append(name);
    if (!entry.isCurrent) {
      const absent = document.createElement('small');
      absent.className = 'navigation-absent';
      absent.textContent = '当前未出现';
      nameCell.append(absent);
    }
    const count = document.createElement('span');
    count.className = 'navigation-count';
    count.textContent = entry.isCurrent ? String(entry.nodeCount) : '—';
    row.append(nameCell, count);
    appendNavigationCheckbox(row, entry, 'highlight', '高亮');
    appendNavigationCheckbox(row, entry, 'route', '路线');
    appendNavigationCheckbox(row, entry, 'direction', '方向');
    rows.append(row);
  });
}

function bindPair(rangeId, numberId, getter, setter) {
  const range = $(rangeId);
  const number = $(numberId);
  const update = value => {
    if (!runtime?.settings || !Number.isFinite(value)) return;
    const settings = editableSettings();
    if (!settings) return;
    setter(settings, value);
    submitSettings(settings);
  };
  range.addEventListener('input', () => update(Number(range.value)));
  number.addEventListener('change', () => update(Number(number.value)));
  return settings => {
    const value = getter(settings);
    range.value = value;
    number.value = value;
  };
}

function bindPercentagePair(rangeId, numberId, getter, setter) {
  return bindPair(
    rangeId,
    numberId,
    settings => Math.round(getter(settings) * 100),
    (settings, value) => setter(settings, value / 100));
}

const visualRenderers = [];

function bindAreaMapToggle(id, key) {
  $(id).addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings) return;
    settings.areaMap[key] = !settings.areaMap[key];
    submitSettings(settings);
  });
}

function configureAreaMapControls() {
  $('areaMapPanelEntryState').addEventListener('change', () => {
    const settings = editableSettings();
    if (!settings) return;
    settings.areaMap.expeditionPanel.expandOnAreaEntry =
      $('areaMapPanelEntryState').value === 'expanded';
    submitSettings(settings);
  });
  bindAreaMapToggle('areaMapExpeditionToggle', 'showExpedition');
  bindAreaMapToggle('areaMapBossToggle', 'showBoss');
  bindAreaMapToggle('areaMapAbyssToggle', 'showAbyss');
  bindAreaMapToggle('areaMapRitualToggle', 'showRitual');
  bindAreaMapToggle('areaMapPollenToggle', 'showPollen');
  bindAreaMapToggle('areaMapOmenAltarToggle', 'showOmenAltar');
  bindAreaMapToggle('areaMapBreachToggle', 'showBreach');
  bindAreaMapToggle('areaMapEssenceToggle', 'showEssence');
  bindAreaMapToggle('areaMapIncursionToggle', 'showIncursion');
  bindAreaMapToggle('areaMapStrongboxToggle', 'showStrongbox');
  bindAreaMapToggle('areaMapRareMonsterToggle', 'showRareMonster');
  bindAreaMapToggle('areaMapRareChestsToggle', 'showRareChests');
  $('areaMapNativeRecipeValuesToggle').addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings) return;
    settings.areaMap.expeditionPanel.showNativeRecipeValues =
      !settings.areaMap.expeditionPanel.showNativeRecipeValues;
    submitSettings(settings);
  });
  $('areaMapAutoHideStandalonePanelToggle').addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings) return;
    settings.areaMap.expeditionPanel.autoHideStandalonePanel =
      !settings.areaMap.expeditionPanel.autoHideStandalonePanel;
    submitSettings(settings);
  });
}

function renderAreaMap() {
  const settings = displayedSettings();
  if (!settings?.areaMap) return;
  $('areaMapPanelEntryState').value =
    settings.areaMap.expeditionPanel.expandOnAreaEntry ? 'expanded' : 'collapsed';
  setToggle($('areaMapExpeditionToggle'), settings.areaMap.showExpedition);
  setToggle($('areaMapBossToggle'), settings.areaMap.showBoss);
  setToggle($('areaMapAbyssToggle'), settings.areaMap.showAbyss);
  setToggle($('areaMapRitualToggle'), settings.areaMap.showRitual);
  setToggle($('areaMapPollenToggle'), settings.areaMap.showPollen);
  setToggle($('areaMapOmenAltarToggle'), settings.areaMap.showOmenAltar);
  setToggle($('areaMapBreachToggle'), settings.areaMap.showBreach);
  setToggle($('areaMapEssenceToggle'), settings.areaMap.showEssence);
  setToggle($('areaMapIncursionToggle'), settings.areaMap.showIncursion);
  setToggle($('areaMapStrongboxToggle'), settings.areaMap.showStrongbox);
  setToggle($('areaMapRareMonsterToggle'), settings.areaMap.showRareMonster);
  setToggle($('areaMapRareChestsToggle'), settings.areaMap.showRareChests);
  setToggle(
    $('areaMapNativeRecipeValuesToggle'),
    settings.areaMap.expeditionPanel.showNativeRecipeValues);
  setToggle(
    $('areaMapAutoHideStandalonePanelToggle'),
    settings.areaMap.expeditionPanel.autoHideStandalonePanel);
}

function bindColor(id, getter, setter, renderers = visualRenderers) {
  const input = $(id);
  input.addEventListener('input', () => {
    const settings = editableSettings();
    if (!settings) return;
    setter(settings, input.value.toUpperCase());
    submitSettings(settings);
  });
  renderers.push(settings => {
    const value = getter(settings).toUpperCase();
    input.value = value;
    document.querySelector(`output[data-for="${id}"]`).textContent = value;
  });
}

function configureVisualControls() {
  bindColor('reachableColor', s => s.edges.reachableColor, (s, v) => s.edges.reachableColor = v);
  bindColor('lockedColor', s => s.edges.lockedColor, (s, v) => s.edges.lockedColor = v);
  bindColor('highlightColor', s => s.highlight.color, (s, v) => s.highlight.color = v);
  bindColor('textColor', s => s.labels.textColor, (s, v) => s.labels.textColor = v);
  bindColor('backgroundColor', s => s.labels.backgroundColor, (s, v) => s.labels.backgroundColor = v);
  visualRenderers.push(bindPair('edgeWidth', 'edgeWidthNumber', s => s.edges.width, (s, v) => s.edges.width = v));
  visualRenderers.push(bindPair('edgeOpacity', 'edgeOpacityNumber', s => s.edges.opacity, (s, v) => s.edges.opacity = v));
  visualRenderers.push(bindPair('highlightWidth', 'highlightWidthNumber', s => s.highlight.width, (s, v) => s.highlight.width = v));
  visualRenderers.push(bindPair('highlightOpacity', 'highlightOpacityNumber', s => s.highlight.opacity, (s, v) => s.highlight.opacity = v));
  visualRenderers.push(bindPair('fontSize', 'fontSizeNumber', s => s.labels.fontSize, (s, v) => s.labels.fontSize = v));
  visualRenderers.push(bindPair('backgroundOpacity', 'backgroundOpacityNumber', s => s.labels.backgroundOpacity, (s, v) => s.labels.backgroundOpacity = v));
  $('backgroundToggle').addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings) return;
    settings.labels.showBackground = !settings.labels.showBackground;
    submitSettings(settings);
  });
}

function renderVisuals() {
  const settings = displayedSettings();
  if (!settings) return;
  visualRenderers.forEach(render => render(settings));
  setToggle($('backgroundToggle'), settings.labels.showBackground);
}

function submitSettings(settings) {
  draftSettings = settings;
  renderSettings();
  post('updateSettings', { settings });
}

function applyTheme() {
  const light = displayedSettings()?.theme === 'Light';
  document.documentElement.dataset.theme = light ? 'light' : 'dark';
  $('themeButton').innerHTML = light ? '<i data-lucide="sun"></i>' : '<i data-lucide="moon"></i>';
  $('themeButton').title = light ? '切换到深色主题' : '切换到浅色主题';
}

function renderSaveStatus() {
  const status = runtime?.persistenceStatus || 'Saved';
  const recovered = runtime?.loadStatus === 'RecoveredInvalid';
  const element = $('saveStatus');
  element.className = status === 'SaveFailed' ? 'failed' : status === 'Saving' ? 'saving' : recovered ? 'recovered' : '';
  element.innerHTML = status === 'SaveFailed'
    ? '<i data-lucide="circle-alert"></i>保存失败'
    : status === 'Saving'
      ? '<i data-lucide="loader-circle"></i>正在保存'
      : recovered
        ? '<i data-lucide="circle-alert"></i>设置文件已损坏，已恢复默认值'
        : '<i data-lucide="check"></i>设置已保存';
}

function updateStaleStatus() {
  if (!runtime || !lastSnapshotAt) return;
  const element = $('overlayStatus');
  const currentStatus = element.dataset.currentStatus || element.textContent;
  const stale = Date.now() - lastSnapshotAt > snapshotStaleAfterMs;
  element.classList.toggle('stale', stale);
  element.textContent = stale ? `状态可能已过期 · ${currentStatus}` : currentStatus;
}

function renderSettings() {
  applyTheme();
  renderAltOverlayMode();
  renderNodeRows();
  renderContents();
  renderNavigation();
  renderAreaMap();
  renderVisuals();
  renderIcons();
}

function render() {
  if (!runtime) return;
  renderProcessBar();
  renderHotkey();
  renderSettings();
  renderSaveStatus();
}

function showPage(page) {
  window.quickAssistUi?.cancelRecording();
  activePage = page;
  document.querySelectorAll('.page').forEach(item => item.classList.toggle('active', item.id === page));
  document.querySelectorAll('.nav-button').forEach(item => item.classList.toggle('active', item.dataset.page === page));
}

function closeProcessMenu() {
  $('processMenu').classList.remove('open');
  $('processSelect').setAttribute('aria-expanded', 'false');
}

async function initialize() {
  catalog = await fetch('assets/content-catalog.json').then(response => response.json());
  window.quickAssistUi?.initialize(() => { hotkeyRecording = false; renderHotkey(); });
  window.pricesUi?.initialize();
  configureAreaMapControls();
  configureVisualControls();
  document.querySelectorAll('.nav-button').forEach(button => button.addEventListener('click', () => showPage(button.dataset.page)));
  document.querySelectorAll('.reset-page').forEach(button => button.addEventListener('click', () => {
    window.pricesUi?.resetDraft();
    window.quickAssistUi?.resetDraft();
    draftSettings = null;
    post('resetPage', { page: button.dataset.reset });
  }));
  $('resetAllButton').addEventListener('click', () => {
    window.pricesUi?.resetDraft();
    window.quickAssistUi?.resetDraft();
    draftSettings = null;
    post('resetAll', {});
  });
  $('hotkeyButton').addEventListener('click', () => {
    window.quickAssistUi?.cancelRecording();
    hotkeyRecording = true;
    localHotkeyError = '';
    renderHotkey();
  });
  $('altModeHold').addEventListener('click', () => updateAltOverlayMode('HoldToHide'));
  $('altModeToggle').addEventListener('click', () => updateAltOverlayMode('ToggleOnPress'));
  $('processSelect').addEventListener('click', event => {
    event.stopPropagation();
    const open = $('processMenu').classList.toggle('open');
    $('processSelect').setAttribute('aria-expanded', String(open));
  });
  $('refreshButton').addEventListener('click', () => post('refreshProcesses'));
  $('overlayButton').addEventListener('click', () => post(runtime?.isOverlayRunning ? 'stopOverlay' : 'startOverlay'));
  $('themeButton').addEventListener('click', () => {
    if (!runtime?.settings) return;
    const settings = editableSettings();
    if (!settings) return;
    settings.theme = settings.theme === 'Light' ? 'Dark' : 'Light';
    submitSettings(settings);
  });
  $('contentSearch').addEventListener('input', renderContents);
  $('selectAllContent').addEventListener('click', () => setAllContent(true));
  $('clearAllContent').addEventListener('click', () => setAllContent(false));
  $('navigationSearch').addEventListener('input', event => {
    navigationSearch = event.target.value;
    renderNavigation();
  });
  $('navigationModeNearest').addEventListener('click', () => updateNavigationMode('Nearest'));
  $('navigationModeAll').addEventListener('click', () => updateNavigationMode('All'));
  $('navigationHideCompletedToggle').addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings?.navigation) return;
    settings.navigation.hideCompletedMaps = !settings.navigation.hideCompletedMaps;
    submitSettings(settings);
  });
  $('clearNavigationRules').addEventListener('click', () => {
    const settings = editableSettings();
    if (!settings?.navigation) return;
    settings.navigation.rules = {};
    submitSettings(settings);
  });
  document.addEventListener('click', closeProcessMenu);
  document.addEventListener('keydown', handleHotkeyRecorder, true);
  window.chrome?.webview?.addEventListener('message', event => {
    const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
    if (message?.version === protocolVersion && message.type === 'quickAssistSnapshot') window.quickAssistUi?.receive(message.payload);
    if (message?.version === protocolVersion && message.type === 'pricesSnapshot') window.pricesUi?.receive(message.payload);
    if (message?.version === protocolVersion && message.type === 'snapshot') {
      const snapshot = message.payload;
      if (draftSettings && settingsEqual(snapshot.settings, draftSettings)) {
        draftSettings = null;
      }
      runtime = snapshot;
      lastSnapshotAt = Date.now();
      render();
    }
  });
  setInterval(updateStaleStatus, 1000);
  renderIcons();
  post('ready');
}

initialize().catch(() => {
  $('saveStatus').className = 'failed';
  $('saveStatus').textContent = '界面资源加载失败';
});
