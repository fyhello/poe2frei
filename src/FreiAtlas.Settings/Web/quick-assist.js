(() => {
  let snapshot = null;
  let draft = null;
  let recording = null;
  let onRecordStart = () => {};
  let localError = '';
  let receivedAt = 0;
  const rules = [{ id: 'health', label: '自动回血' }, { id: 'mana', label: '自动回蓝' }];
  const element = id => document.getElementById(id);
  const settings = () => draft ?? snapshot?.settings;

  function cancelRecording() {
    if (recording) post('setRecoveryKeyRecording', { recording: false });
    recording = null;
    render();
  }

  function submit(id, change) {
    if (!settings()) return;
    draft = JSON.parse(JSON.stringify(settings()));
    Object.assign(draft[id], change);
    localError = '';
    post('updateQuickAssist', { settings: draft });
    render();
  }

  function render() {
    for (const { id } of rules) {
      const rule = settings()?.[id];
      if (!element(`${id}RecoveryEnabled`)) continue;
      element(`${id}RecoveryEnabled`).disabled = !rule;
      setToggle(element(`${id}RecoveryEnabled`), rule?.enabled === true);
      const mode = element(`${id}RecoveryMode`);
      if (rule) mode.value = rule.mode;
      const fixed = rule?.mode === 'Fixed';
      const threshold = element(`${id}RecoveryThreshold`);
      threshold.max = fixed ? '10000000' : '100';
      if (rule && document.activeElement !== threshold) threshold.value = fixed ? rule.fixedValue : rule.percentage;
      element(`${id}RecoveryUnit`).textContent = fixed ? '点' : '%';
      const interval = element(`${id}RecoveryInterval`);
      if (rule && document.activeElement !== interval) interval.value = rule.intervalMilliseconds;
      const key = element(`${id}RecoveryKey`);
      key.textContent = recording === id ? '请按键 · Esc 取消' : `${rule?.key ?? '—'} · 设置`;
      key.classList.toggle('recording', recording === id);
      key.setAttribute('aria-pressed', String(recording === id));
      const status = element(`${id}RecoveryStatus`);
      status.textContent = receivedAt && Date.now() - receivedAt > 2000 ? '连接中断，状态已过期' : snapshot?.state?.[`${id}Status`] ?? '等待角色数据';
      for (const control of [mode, threshold, interval, key]) control.disabled = !rule;
    }
    const portal = settings()?.portalSqueeze;
    const portalEnabled = element('portalSqueezeEnabled');
    if (portalEnabled) {
      setToggle(portalEnabled, portal?.enabled === true);
      portalEnabled.disabled = !portal;
      const portalKey = element('portalSqueezeHotkey');
      portalKey.textContent = recording === 'portal'
        ? '请按键 · Esc 取消'
        : `${portal?.hotkey ?? '—'} · 设置`;
      portalKey.classList.toggle('recording', recording === 'portal');
      portalKey.setAttribute('aria-pressed', String(recording === 'portal'));
      portalKey.disabled = !portal;
      const distance = element('portalSqueezeDistance');
      const attempts = element('portalSqueezeAttempts');
      const timeout = element('portalSqueezeTimeout');
      if (portal && document.activeElement !== distance) distance.value = portal.maxDistanceGrid;
      if (portal && document.activeElement !== attempts) attempts.value = portal.maxAttempts;
      if (portal && document.activeElement !== timeout) timeout.value = portal.confirmTimeoutMilliseconds;
      for (const control of [distance, attempts, timeout]) control.disabled = !portal;
      element('portalSqueezeTrigger').disabled = !portal?.enabled;
      const state = snapshot?.state?.portalSqueeze;
      element('portalSqueezeStatus').textContent = receivedAt && Date.now() - receivedAt > 2000
        ? '连接中断，状态已过期'
        : state?.message ?? '未触发';
    }
    const pickup = settings()?.pickup;
    if (pickup) {
      const toggle = element('pickupEnabled');
      setToggle(toggle, pickup.enabled === true);
      toggle.disabled = false;
      const range = element('pickupRange');
      if (document.activeElement !== range) range.value = pickup.pickupRangeGrid;
      range.disabled = !pickup.enabled;

      const timeout = element('pickupDisappearTimeout');
      if (timeout && document.activeElement !== timeout) timeout.value = pickup.disappearTimeoutMilliseconds ?? 800;
      if (timeout) timeout.disabled = !pickup.enabled;

      const attempts = element('pickupMaxAttempts');
      if (attempts && document.activeElement !== attempts) attempts.value = pickup.maxAttempts ?? 3;
      if (attempts) attempts.disabled = !pickup.enabled;

      const modToggle = element('pickupModifierKeyEnabled');
      if (modToggle) {
        setToggle(modToggle, pickup.modifierKeyEnabled === true);
        modToggle.disabled = !pickup.enabled;
      }
      const modKey = element('pickupModifierKey');
      if (modKey) {
        modKey.textContent = recording === 'pickup'
          ? '请按键 · Esc 取消'
          : `${pickup.modifierKey ?? 'Shift'} · 设置`;
        modKey.classList.toggle('recording', recording === 'pickup');
        modKey.setAttribute('aria-pressed', String(recording === 'pickup'));
        modKey.disabled = !pickup.enabled || !pickup.modifierKeyEnabled;
      }

      const whitelist = element('pickupWhitelist');
      if (document.activeElement !== whitelist) whitelist.value = (pickup.whitelist ?? []).join(', ');
      whitelist.disabled = !pickup.enabled;
      element('pickupStatus').textContent = receivedAt && Date.now() - receivedAt > 2000
        ? '连接中断，状态已过期'
        : snapshot?.state?.pickup?.message ?? '未启用';
    }
    const vitals = snapshot?.state?.vitals;
    const stale = !vitals || Date.now() - Date.parse(vitals.capturedAt) > 1000;
    for (const [id, name] of [['health', 'health'], ['mana', 'mana'], ['shield', 'energyShield']]) {
      const pool = vitals?.[name];
      element(`${id}Vital`).textContent = stale || !pool ? '暂无有效数据' : `${pool.current} / ${pool.availableMaximum}`;
      element(`${id}VitalDetail`).textContent = stale || !pool ? '' : `总上限 ${pool.maximum} · 保留 ${pool.maximum - pool.availableMaximum}`;
    }
    element('quickAssistError').textContent = localError;
  }

  function gesture(event) {
    if (event.metaKey) return null;
    const code = event.code || '';
    let key = /^Key[A-Z]$/.test(code) ? code.slice(3)
      : /^Digit[0-9]$/.test(code) ? code.slice(5)
      : /^Numpad[0-9]$/.test(code) ? code
      : /^F([1-9]|1[0-2])$/.test(code) ? code
      : code === 'Space' ? 'Space' : null;
    if (!key) return null;
    return [event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', key].filter(Boolean).join('+');
  }

  function recordKey(event) {
    if (!recording) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (event.key === 'Escape') { localError = ''; cancelRecording(); return; }
    if (event.repeat) return;

    if (recording === 'pickup') {
      let key = null;
      if (event.key === 'Shift') key = 'Shift';
      else if (event.key === 'Control') key = 'Ctrl';
      else if (event.key === 'Alt') key = 'Alt';
      else if (event.code === 'Space') key = 'Space';
      else if (/^Key[A-Z]$/.test(event.code)) key = event.code.slice(3);
      else if (/^Digit[0-9]$/.test(event.code)) key = event.code.slice(5);

      if (key) {
        cancelRecording();
        submit('pickup', { modifierKey: key });
        return;
      }
    }

    if (['Control', 'Alt', 'Shift', 'Meta'].includes(event.key)) return;
    const key = !event.isComposing && gesture(event);
    if (!key) { localError = '支持字母、数字、小键盘数字、F1–F12、空格，可组合 Ctrl / Alt / Shift。'; render(); return; }
    const id = recording;
    cancelRecording();
    submit(id === 'portal' ? 'portalSqueeze' : id, id === 'portal' ? { hotkey: key } : { key });
  }

  function initialize(recordStart) {
    onRecordStart = recordStart;
    element('recoveryRules').innerHTML = rules.map(({ id, label }) => `
      <section class="settings-band recovery-band">
        <div class="recovery-heading"><h2>${label}</h2><button id="${id}RecoveryEnabled" class="toggle" type="button" role="switch" aria-label="启用${label}" disabled></button></div>
        <div class="recovery-grid">
          <label class="recovery-field"><span>阈值类型</span><select id="${id}RecoveryMode" disabled><option value="Percentage">百分比</option><option value="Fixed">固定数值</option></select></label>
          <label class="recovery-field"><span>低于此值恢复</span><span class="recovery-number"><input id="${id}RecoveryThreshold" type="number" min="1" max="100" step="1" required disabled><span id="${id}RecoveryUnit">%</span></span></label>
          <div class="recovery-field"><span id="${id}RecoveryKeyLabel">恢复按键</span><button id="${id}RecoveryKey" class="secondary-button recovery-key" type="button" aria-labelledby="${id}RecoveryKeyLabel ${id}RecoveryKey" disabled>设置</button></div>
          <label class="recovery-field"><span>最短触发间隔</span><span class="recovery-number"><input id="${id}RecoveryInterval" type="number" min="250" max="60000" step="1" required disabled><span>毫秒</span></span></label>
        </div>
        <p id="${id}RecoveryStatus" class="recovery-status">等待角色数据</p>
      </section>`).join('') + `
      <section class="settings-band recovery-band">
        <div class="recovery-heading"><h2>手动挤门</h2><button id="portalSqueezeEnabled" class="toggle" type="button" role="switch" aria-label="启用挤门" disabled></button></div>
        <div class="recovery-grid">
          <div class="recovery-field"><span>挤门按键</span><button id="portalSqueezeHotkey" class="secondary-button recovery-key" type="button" disabled>设置</button></div>
          <label class="recovery-field"><span>最大距离</span><span class="recovery-number"><input id="portalSqueezeDistance" type="number" min="1" max="500" step="1" required disabled><span>格</span></span></label>
          <label class="recovery-field"><span>最大尝试次数</span><span class="recovery-number"><input id="portalSqueezeAttempts" type="number" min="1" max="5" step="1" required disabled><span>次</span></span></label>
          <label class="recovery-field"><span>确认超时</span><span class="recovery-number"><input id="portalSqueezeTimeout" type="number" min="1000" max="60000" step="100" required disabled><span>毫秒</span></span></label>
        </div>
        <div class="recovery-heading portal-squeeze-actions"><p id="portalSqueezeStatus" class="recovery-status">未触发</p><button id="portalSqueezeTrigger" class="primary-button" type="button">立即挤门</button></div>
      </section>
      <section class="settings-band recovery-band">
        <div class="recovery-heading"><h2>自动拾取</h2><button id="pickupEnabled" class="toggle" type="button" role="switch" aria-label="启用自动拾取" disabled></button></div>
        <p class="recovery-status">移动靠近到物品标签范围内后直接触发拾取；顺手精准点击标签中心，带物品消失确认与超时防卡死闭环；支持按键门禁协同。</p>
        <div class="recovery-grid">
          <label class="recovery-field"><span>触发距离（建议 6~10 格，上限 60）</span><span class="recovery-number"><input id="pickupRange" type="number" min="1" max="60" step="0.5" required disabled><span>格</span></span></label>
          <label class="recovery-field"><span>消失确认超时</span><span class="recovery-number"><input id="pickupDisappearTimeout" type="number" min="100" max="5000" step="50" required disabled><span>毫秒</span></span></label>
          <label class="recovery-field"><span>未消失最大重试</span><span class="recovery-number"><input id="pickupMaxAttempts" type="number" min="1" max="10" step="1" required disabled><span>次</span></span></label>
          <div class="recovery-field"><span>协同按键门禁</span><div style="display:flex;align-items:center;gap:8px;"><button id="pickupModifierKeyEnabled" class="toggle" type="button" role="switch" aria-label="启用按键门禁" disabled></button><button id="pickupModifierKey" class="secondary-button recovery-key" type="button" disabled>设置</button></div></div>
          <label class="recovery-field" style="grid-column: 1 / -1;"><span>物品白名单（支持换行或逗号分隔，宽松模糊匹配）</span><textarea id="pickupWhitelist" rows="4" spellcheck="false" disabled></textarea></label>
        </div>
        <p id="pickupStatus" class="recovery-status">未启用</p>
      </section>`;
    for (const { id } of rules) {
      element(`${id}RecoveryEnabled`).addEventListener('click', () => submit(id, { enabled: !settings()[id].enabled }));
      element(`${id}RecoveryMode`).addEventListener('change', event => submit(id, { mode: event.target.value }));
      element(`${id}RecoveryThreshold`).addEventListener('change', event => {
        if (event.target.reportValidity()) submit(id, { [settings()[id].mode === 'Fixed' ? 'fixedValue' : 'percentage']: Number(event.target.value) });
      });
      element(`${id}RecoveryInterval`).addEventListener('change', event => {
        if (event.target.reportValidity()) submit(id, { intervalMilliseconds: Number(event.target.value) });
      });
      element(`${id}RecoveryKey`).addEventListener('click', () => {
        onRecordStart(); recording = id; localError = '';
        post('setRecoveryKeyRecording', { recording: true });
        render();
      });
    }
    element('portalSqueezeEnabled').addEventListener('click', () => {
      submit('portalSqueeze', { enabled: !settings().portalSqueeze.enabled });
    });
    element('portalSqueezeHotkey').addEventListener('click', () => {
      onRecordStart(); recording = 'portal'; localError = '';
      post('setRecoveryKeyRecording', { recording: true });
      render();
    });
    element('portalSqueezeDistance').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('portalSqueeze', { maxDistanceGrid: Number(event.target.value) });
    });
    element('portalSqueezeAttempts').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('portalSqueeze', { maxAttempts: Number(event.target.value) });
    });
    element('portalSqueezeTimeout').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('portalSqueeze', { confirmTimeoutMilliseconds: Number(event.target.value) });
    });
    element('pickupEnabled').addEventListener('click', () => {
      submit('pickup', { enabled: !settings().pickup.enabled });
    });
    element('pickupRange').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('pickup', { pickupRangeGrid: Number(event.target.value) });
    });
    element('pickupDisappearTimeout').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('pickup', { disappearTimeoutMilliseconds: Number(event.target.value) });
    });
    element('pickupMaxAttempts').addEventListener('change', event => {
      if (event.target.reportValidity()) submit('pickup', { maxAttempts: Number(event.target.value) });
    });
    element('pickupModifierKeyEnabled').addEventListener('click', () => {
      submit('pickup', { modifierKeyEnabled: !settings().pickup.modifierKeyEnabled });
    });
    element('pickupModifierKey').addEventListener('click', () => {
      onRecordStart(); recording = 'pickup'; localError = '';
      post('setRecoveryKeyRecording', { recording: true });
      render();
    });
    element('pickupWhitelist').addEventListener('change', event => {
      const whitelist = event.target.value.split(/[\r\n,，]+/).map(value => value.trim()).filter(Boolean);
      submit('pickup', { whitelist });
    });
    element('portalSqueezeTrigger').addEventListener('click', () => post('triggerPortalSqueeze'));
    document.addEventListener('keydown', recordKey, true);
    window.addEventListener('blur', cancelRecording);
    setInterval(render, 500);
    render();
  }

  window.quickAssistUi = {
    initialize, cancelRecording,
    resetDraft() { draft = null; localError = ''; cancelRecording(); },
    receive(value) {
      snapshot = value;
      receivedAt = Date.now();
      if (value.error) { localError = value.error; draft = null; cancelRecording(); }
      else if (draft && JSON.stringify(draft) === JSON.stringify(value.settings)) draft = null;
      render();
    }
  };
})();
