(() => {
  let pendingLeague = null;
  let payload = null;
  const element = id => document.getElementById(id);
  const post = (type, value = {}) => window.chrome?.webview?.postMessage({ version: 1, type, payload: value });

  function render() {
    if (!payload) return;
    const { settings, state, error } = payload;
    const prices = state.prices;
    const selected = settings.leagueId;
    const leagues = state.leagues || [];
    const select = element('priceLeague');
    const options = leagues.map(league => new Option(league.displayName, league.id));
    if (!leagues.some(league => league.id === selected)) {
      const saved = new Option(`${selected}（已保存，当前列表不可用）`, selected);
      saved.disabled = true;
      options.push(saved);
    }
    select.replaceChildren(...options);
    select.value = pendingLeague || selected;
    select.disabled = false;
    const busy = prices.isRefreshing || state.isUpdatingCatalog;
    element('refreshPrices').disabled = busy;
    element('refreshPrices').textContent = busy ? '正在刷新…' : '刷新物价与赛季列表';
    const name = leagues.find(league => league.id === prices.leagueId)?.displayName || prices.leagueId;
    element('priceActiveLeague').textContent = name;
    element('priceStatus').textContent = prices.itemCount === 0 && !prices.isRefreshing
      ? `暂无可用价格。${prices.message}` : prices.message;
    element('priceUpdated').textContent = prices.fetchedAtUtc
      ? new Date(prices.fetchedAtUtc).toLocaleString('zh-CN', { hour12: false }) : '尚未取得';
    element('priceRate').textContent = prices.exaltedPerDivine > 0 ? `1 D = ${prices.exaltedPerDivine} E` : '暂无有效汇率';
    element('priceSources').replaceChildren(...(prices.sources || []).map(source => {
      const row = document.createElement('div');
      row.className = 'price-source';
      const nameElement = document.createElement('strong');
      nameElement.textContent = source.name;
      const detail = document.createElement('span');
      detail.textContent = `${source.itemCount} 条报价 · ${source.message}`;
      row.append(nameElement, detail);
      return row;
    }));
    element('priceCatalogError').textContent = state.catalogError || '';
    element('priceError').textContent = error || '';
  }

  window.pricesUi = {
    initialize() {
      element('priceLeague').addEventListener('change', event => {
        pendingLeague = event.target.value;
        element('priceStatus').textContent = '正在切换物价区服…';
        element('priceActiveLeague').textContent = event.target.selectedOptions[0].text;
        element('priceUpdated').textContent = '等待该区服数据';
        element('priceRate').textContent = '—';
        element('priceSources').replaceChildren();
        post('selectPriceLeague', { leagueId: pendingLeague });
      });
      element('refreshPrices').addEventListener('click', () => {
        element('refreshPrices').disabled = true;
        element('refreshPrices').textContent = '正在刷新…';
        post('refreshPrices');
      });
    },
    resetDraft() { pendingLeague = null; },
    receive(next) {
      if (!next?.settings || !next?.state?.prices) return;
      if (pendingLeague && next.settings.leagueId !== pendingLeague && !next.error) return;
      pendingLeague = null;
      payload = next;
      render();
    }
  };
})();
