(() => {
  let pendingLeague = null;
  let payload = null;
  let items = [];
  let page = 1;
  const pageSize = 100;
  const element = id => document.getElementById(id);
  const post = (type, value = {}) => window.chrome?.webview?.postMessage({ version: 1, type, payload: value });

  const categoryLabels = {
    currency: '通货',
    expedition: '远征',
    runes: '符文',
    verisium: '维里西姆',
    uncutgems: '未切割宝石'
  };

  const categoryLabel = value => {
    const key = String(value || '').trim().toLowerCase();
    return categoryLabels[key] || (value || '未分类');
  };

  const formatNumber = value => new Intl.NumberFormat('zh-CN', {
    maximumFractionDigits: 2
  }).format(Number(value) || 0);

  const formatPrice = item => {
    const divine = Number(item.divineValue) || 0;
    if (divine <= 0) return '—';
    return divine >= 0.5
      ? `${formatNumber(divine)} D`
      : `${formatNumber(item.exaltedValue)} E`;
  };

  const itemMatches = (item, query) => {
    if (!query) return true;
    const names = [item.name, item.itemId, ...(Array.isArray(item.aliases) ? item.aliases : [])];
    return names.some(value => String(value || '').toLowerCase().includes(query));
  };

  const filteredItems = () => {
    const query = (element('priceSearch')?.value || '').trim().toLowerCase();
    const category = element('priceCategoryFilter')?.value || '';
    const source = element('priceSourceFilter')?.value || '';
    const list = items.filter(item => {
      if (!itemMatches(item, query)) return false;
      if (category && String(item.category || '').toLowerCase() !== category.toLowerCase()) return false;
      if (source && String(item.source || '').toLowerCase() !== source.toLowerCase()) return false;
      return true;
    });

    const sort = element('priceSort')?.value || 'price-desc';
    list.sort((a, b) => {
      if (sort === 'name') return String(a.name || '').localeCompare(String(b.name || ''), 'zh-CN');
      const left = Number(a.divineValue) || 0;
      const right = Number(b.divineValue) || 0;
      return sort === 'price-asc' ? left - right : right - left;
    });
    return list;
  };

  const syncFilterOptions = () => {
    const categorySelect = element('priceCategoryFilter');
    const sourceSelect = element('priceSourceFilter');
    if (!categorySelect || !sourceSelect) return;

    const previousCategory = categorySelect.value;
    const categoryMap = new Map();
    items.forEach(item => {
      const raw = String(item.category || '').trim();
      if (raw && !categoryMap.has(raw.toLowerCase())) categoryMap.set(raw.toLowerCase(), raw);
    });
    const categories = [...categoryMap.values()]
      .sort((a, b) => categoryLabel(a).localeCompare(categoryLabel(b), 'zh-CN'));
    categorySelect.replaceChildren(
      new Option('全部分类', ''),
      ...categories.map(category => new Option(categoryLabel(category), category)));
    categorySelect.value = categories.includes(previousCategory) ? previousCategory : '';

    const previousSource = sourceSelect.value;
    const sourceMap = new Map();
    items.forEach(item => {
      const raw = String(item.source || '').trim();
      if (raw && !sourceMap.has(raw.toLowerCase())) sourceMap.set(raw.toLowerCase(), raw);
    });
    const sources = [...sourceMap.values()].sort();
    sourceSelect.replaceChildren(
      new Option('全部来源', ''),
      ...sources.map(source => new Option(source, source)));
    sourceSelect.value = sources.includes(previousSource) ? previousSource : '';
  };

  const renderTable = () => {
    const body = element('priceTableBody');
    const empty = element('priceTableEmpty');
    const count = element('priceItemCount');
    const pageInfo = element('pricePageInfo');
    const prev = element('pricePrev');
    const next = element('priceNext');
    if (!body || !empty || !count || !pageInfo || !prev || !next) return;

    syncFilterOptions();
    const list = filteredItems();
    const totalPages = Math.max(1, Math.ceil(list.length / pageSize));
    page = Math.min(Math.max(page, 1), totalPages);
    const start = (page - 1) * pageSize;
    const visible = list.slice(start, start + pageSize);
    const injectable = items.filter(item => (Number(item.exaltedValue) || 0) >= 1).length;

    count.textContent = items.length === 0
      ? '—'
      : `共 ${items.length} 条 · 可注入 ${injectable} 条（≥1E）`;

    const fragment = document.createDocumentFragment();
    visible.forEach(item => {
      const row = document.createElement('tr');

      const nameCell = document.createElement('td');
      const name = document.createElement('div');
      name.className = 'price-item-name';
      name.textContent = item.name || item.itemId || '—';
      nameCell.appendChild(name);
      const aliases = (Array.isArray(item.aliases) ? item.aliases : [])
        .filter(alias => alias && alias !== item.name);
      if (aliases.length > 0) {
        const alias = document.createElement('div');
        alias.className = 'price-item-alias';
        alias.textContent = aliases.join(' / ');
        nameCell.appendChild(alias);
      }

      const categoryCell = document.createElement('td');
      categoryCell.textContent = categoryLabel(item.category);

      const priceCell = document.createElement('td');
      priceCell.className = 'price-amount';
      priceCell.textContent = formatPrice(item);

      const divineCell = document.createElement('td');
      divineCell.className = 'price-amount';
      divineCell.textContent = `${formatNumber(item.divineValue)} D`;

      const sourceCell = document.createElement('td');
      sourceCell.textContent = item.source || '—';

      row.append(nameCell, categoryCell, priceCell, divineCell, sourceCell);
      fragment.appendChild(row);
    });

    body.replaceChildren(fragment);
    empty.classList.toggle('hidden', list.length > 0);
    pageInfo.textContent = list.length === 0
      ? '—'
      : `${page} / ${totalPages} · 显示 ${start + 1}-${Math.min(start + pageSize, list.length)} 条`;
    prev.disabled = page <= 1;
    next.disabled = page >= totalPages;
  };

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
    renderTable();
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
        page = 1;
        post('selectPriceLeague', { leagueId: pendingLeague });
      });
      element('refreshPrices').addEventListener('click', () => {
        element('refreshPrices').disabled = true;
        element('refreshPrices').textContent = '正在刷新…';
        post('refreshPrices');
      });

      element('priceSearch').addEventListener('input', () => {
        page = 1;
        renderTable();
      });
      element('priceCategoryFilter').addEventListener('change', () => {
        page = 1;
        renderTable();
      });
      element('priceSourceFilter').addEventListener('change', () => {
        page = 1;
        renderTable();
      });
      element('priceSort').addEventListener('change', () => {
        page = 1;
        renderTable();
      });
      element('pricePrev').addEventListener('click', () => {
        page = Math.max(1, page - 1);
        renderTable();
      });
      element('priceNext').addEventListener('click', () => {
        page += 1;
        renderTable();
      });
    },
    resetDraft() {
      pendingLeague = null;
      page = 1;
      if (element('priceSearch')) element('priceSearch').value = '';
      if (element('priceCategoryFilter')) element('priceCategoryFilter').value = '';
      if (element('priceSourceFilter')) element('priceSourceFilter').value = '';
      if (element('priceSort')) element('priceSort').value = 'price-desc';
    },
    receive(next) {
      if (!next?.settings || !next?.state?.prices) return;
      if (pendingLeague && next.settings.leagueId !== pendingLeague && !next.error) return;
      pendingLeague = null;
      payload = next;
      items = Array.isArray(next.items) ? next.items : [];
      page = 1;
      render();
    }
  };
})();
