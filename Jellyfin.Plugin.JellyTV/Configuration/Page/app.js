  var JellyTV = {
      pluginId: 'f8a3c1d7-9b2e-4a5f-8c6d-3e1b7f4a2d9c',
      config: null,
      editingChannelId: null,
      sortKey: 'name',
      sortDir: 1,

      // Buscador y orden por columna para tablas que se rellenan con innerHTML: se reaplican solos tras cada render.
      enhanceGrid: function(bodyId){
          var tb = document.getElementById(bodyId);
          var table = tb && tb.closest('table');
          if(!tb || !table || table.getAttribute('data-grid')) return;
          table.setAttribute('data-grid', '1');
          var input = document.createElement('input');
          input.type = 'search';
          input.placeholder = 'Buscar…';
          input.setAttribute('aria-label', 'Buscar en la tabla');
          input.style.cssText = 'margin:0 0 8px;padding:6px 10px;width:100%;max-width:320px;box-sizing:border-box;background:transparent;color:inherit;border:1px solid var(--jt-border-soft);border-radius:8px;';
          table.parentNode.insertBefore(input, table);
          // Las tablas de canales (la primera columna es el canal) salen en orden alfabético; los proxies, en el orden en que se agregaron.
          var state = { col: /^(canal|channel)$/i.test(((table.querySelector('thead th') || {}).textContent || '').trim()) ? 0 : -1, dir: 1 };
          var ths = [].slice.call(table.querySelectorAll('thead th'));
          var val = function(r, c){ var td = r.cells[c]; if(!td) return ''; return (td.getAttribute('data-v') !== null ? td.getAttribute('data-v') : td.textContent).trim().toLowerCase(); };
          var apply = function(){
              obs.disconnect();
              var rows = [].slice.call(tb.rows).filter(function(r){ return !r.querySelector('.jt-empty'); });
              var q = input.value.trim().toLowerCase();
              rows.forEach(function(r){ r.style.display = (!q || r.textContent.toLowerCase().indexOf(q) > -1) ? '' : 'none'; });
              if(state.col >= 0){
                  rows.sort(function(a, b){
                      var x = val(a, state.col), y = val(b, state.col);
                      var nx = parseFloat(x), ny = parseFloat(y);
                      var num = !isNaN(nx) && !isNaN(ny) && /^-?\d+(\.\d+)?$/.test(x) && /^-?\d+(\.\d+)?$/.test(y);
                      return (num ? nx - ny : x.localeCompare(y, undefined, { numeric: true })) * state.dir;
                  });
                  rows.forEach(function(r){ tb.appendChild(r); });
              }
              ths.forEach(function(th, i){
                  var ind = th.querySelector('.jt-sort-ind');
                  if(ind){ ind.textContent = state.col === i ? (state.dir > 0 ? ' ▲' : ' ▼') : ' ↕'; ind.style.opacity = state.col === i ? '1' : '0.3'; }
              });
              obs.observe(tb, { childList: true });
          };
          var obs = new MutationObserver(apply);
          ths.forEach(function(th, i){
              if(/^(acciones|actions)$/i.test(th.textContent.trim())) return;
              th.style.cursor = 'pointer';
              th.insertAdjacentHTML('beforeend', '<span class="jt-sort-ind" style="opacity:.3"> ↕</span>');
              th.addEventListener('click', function(){
                  if(state.col === i){ state.dir = -state.dir; } else { state.col = i; state.dir = 1; }
                  apply();
              });
          });
          input.addEventListener('input', apply);
          apply();
      },
      init: function(){
          var self = this;
          jtStartI18n(document.querySelector('#JellyTVConfigPage'));
          ['epgCoverageBody', 'proxiesTableBody'].forEach(function(id){ self.enhanceGrid(id); });
          document.querySelector('#JellyTVConfigPage').addEventListener('pageshow', function(){
              Dashboard.showLoadingMsg();
              ApiClient.getPluginConfiguration(self.pluginId).then(function(cfg){
                  self.config = cfg;
                  self.config.Channels = self.config.Channels || [];
                  self.config.Proxies = self.config.Proxies || [];
                  self.config.EpgSources = self.config.EpgSources || [];
                  self.renderAll();
                  Dashboard.hideLoadingMsg();
                  self.resumeProgress();
              });
          });

          document.querySelectorAll('.jt-tab-btn').forEach(function(b){
              b.addEventListener('click', function(){ self.switchTab(b.dataset.tab); });
          });
          document.querySelectorAll('[data-goto-tab]').forEach(function(b){
              b.addEventListener('click', function(){ self.switchTab(b.dataset.gotoTab); });
          });
          document.querySelectorAll('[data-diag]').forEach(function(b){
              b.addEventListener('click', function(){ self.openDiag(b.dataset.diag); });
          });
          self.initDialog();
          document.getElementById('catExit').addEventListener('change', function(){
              self.catalog = self.catalog || { sel: {}, channels: {}, filter: {} };
              self.catalog.channels = {};
              self.loadCatalog();
          });

          var chSearch = document.getElementById('chSearch');
          if(chSearch) chSearch.addEventListener('input', function(){ self.renderChannelTable(); });
          document.getElementById('btnAddChannel').addEventListener('click', function(){ self.openChannelModal(null); });
          document.getElementById('btnEnrichIdentity').addEventListener('click', function(){ self.enrichIdentities(); });
          document.getElementById('btnExportM3u').addEventListener('click', function(){ self.exportM3u(); });
          document.getElementById('btnImportM3u').addEventListener('click', function(){ document.getElementById('m3uImportFile').click(); });
          document.getElementById('m3uImportFile').addEventListener('change', function(e){
              var file = e.target.files && e.target.files[0];
              e.target.value = '';
              if(!file) return;
              var reader = new FileReader();
              reader.onload = function(){ self.importM3u(String(reader.result || '')); };
              reader.onerror = function(){ JellyTV.notify('No se pudo leer el archivo.','err'); };
              reader.readAsText(file);
          });
          document.getElementById('btnIdSearch').addEventListener('click', function(){ self.searchIdentity(); });
          document.getElementById('btnIdClear').addEventListener('click', function(){ self.modalIdentity = null; self.renderIdentityCard(); });
          document.getElementById('chLogoUrl').addEventListener('input', function(){ self.renderIdentityCard(); });
          document.getElementById('LiveTvMode').addEventListener('change', function(){ self.updateLiveTvHint(); });
          var ovTuner = document.getElementById('btnCopyOvTuner');
          if(ovTuner) ovTuner.addEventListener('click', function(){ self.copy(self.tunerUrl()); });
          var ovEpg = document.getElementById('btnCopyOvEpg');
          if(ovEpg) ovEpg.addEventListener('click', function(){ self.copy(JellyTV.publicBase() + '/JellyTV/epg.xml'); });
          var btnRelayKey = document.getElementById('btnCopyRelayKey');
          if(btnRelayKey) btnRelayKey.addEventListener('click', function(){ self.copy(self.relayKey()); });
          var btnRotateRelayKey = document.getElementById('btnRotateRelayKey');
          if(btnRotateRelayKey) btnRotateRelayKey.addEventListener('click', function(){
              self.confirm('Rotar relay key', 'Las URLs actuales de tuner y streams dejarán de funcionar. Deberás actualizar la URL del tuner en Live TV con la nueva clave.', function(){
                  Dashboard.showLoadingMsg();
                  ApiClient.ajax({type:'POST', url:'/JellyTV/relay-key/rotate', dataType:'json'}).then(function(){
                      Dashboard.hideLoadingMsg();
                      self.reloadConfig(function(){ self.notify('Clave rotada. Actualiza la URL del tuner en Live TV.','ok'); });
                  }, function(){ Dashboard.hideLoadingMsg(); self.notify('No se pudo rotar la clave.','err'); });
              }, 'Rotar');
          });
          var btnAddSource = document.getElementById('btnAddSource');
          if(btnAddSource) btnAddSource.addEventListener('click', function(){ self.addSource(); });
          var btnShowProxy = document.getElementById('btnShowProxyEditor');
          if(btnShowProxy) btnShowProxy.addEventListener('click', function(){ self.showProxyEditor(); });
          var btnSaveProxy = document.getElementById('btnSaveProxy');
          if(btnSaveProxy) btnSaveProxy.addEventListener('click', function(){ self.saveProxy(); });
          var btnTestProxy = document.getElementById('btnTestProxyEditor');
          if(btnTestProxy) btnTestProxy.addEventListener('click', function(){ self.testProxyEditor(); });
          var btnCancelProxy = document.getElementById('btnCancelProxy');
          if(btnCancelProxy) btnCancelProxy.addEventListener('click', function(){ document.getElementById('proxyEditor').style.display = 'none'; });
          document.getElementById('btnSaveChannel').addEventListener('click', function(){ self.saveChannel(); });
          document.getElementById('btnCancelChannel').addEventListener('click', function(){ self.closeChannelModal(); });
          var chModal = document.getElementById('channelModal');
          var closeBtn = document.getElementById('chModalClose');
          if(closeBtn) closeBtn.addEventListener('click', function(){ self.closeChannelModal(); });
          document.addEventListener('keydown', function(e){
              if(e.key === 'Escape' && chModal && chModal.classList.contains('open')) self.closeChannelModal();
          });
          document.getElementById('btnSaveSettings').addEventListener('click', function(){ self.saveSettings(); });
          var btnCov = document.getElementById('btnRefreshCoverage');
          if(btnCov) btnCov.addEventListener('click', function(){ self.loadEpgCoverage(); });
          var btnRefreshStorage = document.getElementById('btnRefreshStorage');
          if(btnRefreshStorage) btnRefreshStorage.addEventListener('click', function(){ self.loadStorageInfo(); });
          var liveTvMode = document.getElementById('LiveTvMode');
          if(liveTvMode) liveTvMode.addEventListener('change', function(){ self.updateLiveTvHint(); });
          var btnDetectTz = document.getElementById('btnDetectTz');
          if(btnDetectTz) btnDetectTz.addEventListener('click', function(){ self.useMyTimezone(); });
          document.getElementById('btnCopyTuner').addEventListener('click', function(){ self.copy(self.tunerUrl()); });
          document.getElementById('btnScanChannels').addEventListener('click', function(){
              self.confirm('Verificar la salud de los canales', 'Se comprobar\u00E1 cada fuente de cada canal, tambi\u00E9n las de los canales apagados (sin encenderlos). Los canales ca\u00EDdos se desactivan solos. Puede tardar unos minutos.', function(){
                  ApiClient.ajax({type:'POST',url:'/JellyTV/health-check'}).then(function(){
                      self.trackHealth();
                  },function(e){
                      if(e && e.status === 409){ JellyTV.notify('Ya hay una revisi\u00F3n en curso.','warn'); self.trackHealth(); return; }
                      JellyTV.notify('No se pudo iniciar la verificaci\u00F3n.','err');
                  });
              }, 'Verificar');
          });
          var chCat = document.getElementById('chCatFilter');
          var btnRefresh = document.getElementById('btnRefreshChannels');
          if(chCat) chCat.addEventListener('change', function(){ JellyTV.renderChannelTable(); });
          if(btnRefresh) btnRefresh.addEventListener('click', function(){
              self.confirm('Actualizar canales en Jellyfin', 'Pide a Jellyfin que vuelva a leer los canales y la gu\u00EDa de JellyTV. Puede interrumpir unos segundos la lista de Live TV.', function(){
                  Dashboard.showLoadingMsg();
                  ApiClient.ajax({type:'POST',url:'/JellyTV/refresh-guide'}).then(function(){
                      Dashboard.hideLoadingMsg();
                      JellyTV.notify('Actualizaci\u00F3n de canales/gu\u00EDa iniciada. Puede tardar unos segundos.','ok');
                  },function(){ Dashboard.hideLoadingMsg(); JellyTV.notify('No se pudo iniciar la actualizaci\u00F3n.','err'); });
              }, 'Actualizar');
          });
          document.getElementById('btnAddEpgSource').addEventListener('click', function(){ self.openEpgEditor(null); });
          document.getElementById('btnSaveEpgSource').addEventListener('click', function(){ self.saveEpgSource(); });
          document.getElementById('btnCancelEpgSource').addEventListener('click', function(){ self.cancelEpgEditor(); });
          var btnSyncEpg = document.getElementById('btnSyncEpg');
          if(btnSyncEpg) btnSyncEpg.addEventListener('click', function(){ self.syncEpg(); });
          var btnTestGuide = document.getElementById('btnTestGuide');
          if(btnTestGuide) btnTestGuide.addEventListener('click', function(){ self.testGuide(); });
          var btnSyncEpgClean = document.getElementById('btnSyncEpgClean');
          if(btnSyncEpgClean) btnSyncEpgClean.addEventListener('click', function(){ self.syncEpgClean(); });
          var btnCopyEpg = document.getElementById('btnCopyEpg');
          if(btnCopyEpg) btnCopyEpg.addEventListener('click', function(){ self.copyEpgUrl(); });
      },

      renderAll: function(){
          this.renderDashboard();
          this.renderOverview();
          this.renderChannelTable();
          this.renderOffReady();
          this.renderProxyTable();
          this.renderEpgList();
          this.renderSettings();
      },

      renderDashboard: function(){
          var ch = this.config.Channels || [];
          var total = ch.length;
          var enabled = ch.filter(function(c){return c.IsEnabled;}).length;
          var alive = ch.filter(function(c){return c.LastCheckAlive === true;}).length;
          var dead = ch.filter(function(c){return c.LastCheckAlive === false;}).length;
          var epg = (this.config.EpgSources || []).filter(function(s){return s.IsEnabled !== false;}).length;
          var proxies = (this.config.Proxies || []).length;
          var h = '';
          h += '<div class="jt-stat blue"><div class="jt-stat-val">'+total+'</div><div class="jt-stat-lbl">Canales</div></div>';
          h += '<div class="jt-stat good"><div class="jt-stat-val">'+enabled+'</div><div class="jt-stat-lbl">Activos</div></div>';
          h += '<div class="jt-stat good"><div class="jt-stat-val">'+alive+'</div><div class="jt-stat-lbl">Vivos</div></div>';
          h += '<div class="jt-stat '+(dead>0?'bad':'')+'"><div class="jt-stat-val">'+dead+'</div><div class="jt-stat-lbl">Caídos</div></div>';
          h += '<div class="jt-stat"><div class="jt-stat-val">'+epg+'</div><div class="jt-stat-lbl">Fuentes EPG</div></div>';
          h += '<div class="jt-stat"><div class="jt-stat-val">'+proxies+'</div><div class="jt-stat-lbl">Proxies</div></div>';
          document.getElementById('channelDash').innerHTML = h;
          this.updateBanner(total, alive, dead, epg);
      },

      renderOverview: function(){
          var tu = document.getElementById('ovTunerUrl');
          if(tu) tu.textContent = this.tunerUrl();
          var e = document.getElementById('ovEpgUrl');
          if(e) e.textContent = JellyTV.publicBase() + '/JellyTV/epg.xml';
          var box = document.getElementById('ovEpgStatus');
          if(!box) return;
          var sources = this.config.EpgSources || [];
          if(!sources.length){
              box.innerHTML = 'Sin fuentes EPG configuradas.';
              return;
          }
          var last = null;
          sources.forEach(function(s){ if(s.LastSyncedAt && (!last || new Date(s.LastSyncedAt) > new Date(last))) last = s.LastSyncedAt; });
          var errors = sources.filter(function(s){ return !!s.LastSyncError; });
          var enabled = sources.filter(function(s){ return s.IsEnabled !== false; }).length;
          var html = '<div>' + t('<b>{0}</b> fuente(s), <b>{1}</b> habilitada(s).', sources.length, enabled) + '</div>';
          html += '<div style="margin-top:4px;">' + t('Última sync: <b>{0}</b>', last ? new Date(last).toLocaleString() : t('nunca')) + '</div>';
          if(errors.length){ html += '<div style="margin-top:4px;color:var(--jt-bad);">' + t('{0} fuente(s) con error.', errors.length) + '</div>'; }
          box.innerHTML = html;
      },

      // Canales apagados por el usuario cuya última revisión salió viva: ya se pueden encender.
      renderOffReady: function(){
          var box = document.getElementById('healthOffReady');
          if(!box) return;
          var ready = (this.config.Channels || []).filter(function(c){ return !c.IsEnabled && !c.AutoDisabled && c.LastCheckAlive === true; });
          box.innerHTML = ready.length
              ? '<span style="color:var(--jt-good)">✓ ' + t('{0} canal(es) apagado(s) ya responde(n) y se puede(n) encender:', ready.length) + '</span> ' + ready.map(function(c){ return JellyTV.esc(c.Name); }).join(', ') + '.'
              : '';
      },

      // Sigue en vivo una tarea larga del servidor y pinta su barra con el avance real que reporta.
      trackProgress: function(o){
          var self = this;
          var wrap = document.getElementById(o.box), bar = document.getElementById(o.bar), txt = document.getElementById(o.text);
          if(!wrap || !bar || !txt || this['_track_' + o.box]){ return; }
          this['_track_' + o.box] = true;
          var finish = function(){ self['_track_' + o.box] = false; };
          var tick = function(){
              ApiClient.getJSON(ApiClient.getUrl(o.url)).then(function(p){
                  if(p.running){
                      o.fresh = true; // si lo vimos correr, al terminar toca refrescar y avisar
                      var known = p.total > 0, pct = known ? Math.min(100, Math.round(p.done * 100 / p.total)) : 0;
                      wrap.style.display = '';
                      wrap.classList.toggle('indet', !known);
                      bar.style.width = known ? pct + '%' : '';
                      bar.textContent = known && pct >= 8 ? pct + ' %' : '';
                      txt.textContent = o.label(p, pct);
                      setTimeout(tick, 1200);
                      return;
                  }
                  finish();
                  if(o.fresh){
                      bar.style.width = '100%';
                      bar.textContent = '100 %';
                      wrap.classList.remove('indet');
                      setTimeout(function(){ wrap.style.display = 'none'; }, 2500);
                  } else {
                      wrap.style.display = 'none';
                  }
                  var when = p.finishedAt ? new Date(p.finishedAt).toLocaleString() : '';
                  txt.textContent = p.finishedAt ? (o.fresh ? t('Terminada ({0}): {1}.', when, p.summary || t('sin datos')) : t('Última ejecución ({0}): {1}.', when, p.summary || t('sin datos'))) : '';
                  if(o.fresh && o.done){ o.done(p); }
              }, finish);
          };
          tick();
      },

      trackHealth: function(fresh){
          var self = this;
          this.trackProgress({
              url: 'JellyTV/health-check/status', box: 'healthProgress', bar: 'healthProgressBar', text: 'healthProgressText', fresh: fresh !== false,
              label: function(p, pct){ return p.total ? t('Comprobando fuentes: {0} de {1} ({2} %)', p.done, p.total, pct) : t('Preparando la revisión…'); },
              done: function(){ self.reloadConfig(); }
          });
      },

      trackEpg: function(fresh){
          var self = this;
          this.trackProgress({
              url: 'JellyTV/epg/sync/status', box: 'epgProgress', bar: 'epgProgressBar', text: 'epgProgressText', fresh: fresh !== false,
              label: function(p, pct){ return (p.total ? t('Paso {0} de {1} ({2} %) · ', Math.min(p.done + 1, p.total), p.total, pct) : '') + p.phase + (p.detail ? ': ' + p.detail : '…'); },
              done: function(){ self.reloadConfig(function(){ self.loadEpgCoverage(); }); }
          });
      },

      // Al abrir el panel: si hay una tarea corriendo se engancha a ella; si no, muestra el resultado de la última.
      resumeProgress: function(){
          this.trackHealth(false);
          this.trackEpg(false);
      },

      loadCatalog: function(){
          var self = this;
          self.catalog = self.catalog || { sel: {}, channels: {}, filter: {} };
          var exit = document.getElementById('catExit');
          var current = exit.value;
          var countries = [];
          (self.config.Proxies || []).forEach(function(p){ var c = (p.Country||'').toUpperCase(); if(c && countries.indexOf(c) < 0) countries.push(c); });
          exit.innerHTML = '<option value="">La IP de este servidor</option>' + countries.map(function(c){ return '<option value="'+c+'">'+t('Proxy de {0}', c)+'</option>'; }).join('');
          exit.value = countries.indexOf(current) >= 0 ? current : '';
          ApiClient.ajax({ type:'GET', url:'/JellyTV/providers', dataType:'json' }).then(function(list){
              var box = document.getElementById('catalogProviders');
              var open = {};
              box.querySelectorAll('details[open]').forEach(function(d){ open[d.dataset.p] = true; });
              box.innerHTML = (list || []).map(function(p){
                  return '<details data-p="'+self.esc(p.id)+'"'+(open[p.id] ? ' open' : '')+'><summary><span>'+self.esc(p.name)+'</span>' +
                      '<span class="jt-badge '+(p.imported ? 'jt-badge-green' : 'jt-badge-gray')+'">'+t('{0} importado(s)', p.imported)+'</span>' +
                      '<span class="jt-muted jt-small jt-acc-note">'+self.esc(jtProviderNote(p.id))+'</span></summary><div class="jt-acc-body" id="catBody-'+self.esc(p.id)+'"></div></details>';
              }).join('');
              box.querySelectorAll('details').forEach(function(d){
                  d.addEventListener('toggle', function(){ if(d.open && !self.catalog.channels[d.dataset.p]){ self.loadProviderChannels(d.dataset.p, false); } });
                  if(d.open){ self.loadProviderChannels(d.dataset.p, false); }
              });
          }, function(){ self.notify('No se pudo leer la lista de proveedores.', 'err'); });
      },
      loadProviderChannels: function(id, refresh){
          var self = this;
          var body = document.getElementById('catBody-' + id);
          if(!body) return;
          body.innerHTML = '<div class="jt-muted jt-small" style="padding:10px 0;">Consultando el catálogo…</div>';
          var exit = document.getElementById('catExit').value;
          ApiClient.ajax({ type:'GET', url:'/JellyTV/providers/' + encodeURIComponent(id) + '/channels?exit=' + encodeURIComponent(exit) + (refresh ? '&refresh=true' : ''), dataType:'json' }).then(function(list){
              self.catalog.channels[id] = list || [];
              self.catalog.sel[id] = {};
              self.renderProviderTable(id);
          }, function(xhr){
              var msg = 'No se pudo leer el catálogo.';
              try { var e = JSON.parse(xhr.responseText); if(e && e.error) msg = jtServerMsg(e.error); } catch(x){}
              body.innerHTML = '<div class="jt-status err" style="display:block;">'+self.esc(msg)+'</div><div class="jt-toolbar"><button type="button" is="emby-button" class="raised emby-button jt-secondary" data-act="reload"><span>Reintentar</span></button></div>';
              body.querySelector('[data-act="reload"]').addEventListener('click', function(){ self.loadProviderChannels(id, true); });
          });
      },
      // Estado de un canal del catálogo. Uno que ya tienes (mismo nombre) se agrega como respaldo de ese al marcarlo: solo cambia el badge,
      // sin elegir nada más, para no atar un canal a otro por accidente.
      providerState: function(provId, c, checked){
          var self = this;
          if(c.imported){ return '<span class="jt-badge jt-badge-green">Importado</span>'; }
          if(!c.similarId){ return ''; }
          var own = (self.config.Channels || []).filter(function(o){ return o.Id === c.similarId; })[0];
          var title = own ? ' title="'+t('Canal existente: «{0}»', self.esc(own.Name))+'"' : '';
          return checked
              ? '<span class="jt-badge jt-badge-blue"'+title+'>Se agregará como respaldo</span>'
              : '<span class="jt-badge jt-badge-yellow"'+title+'>Ya tienes uno igual</span>';
      },
      // Al marcar o desmarcar un canal se vuelve a pintar solo su celda de estado.
      refreshProviderState: function(provId, channelId, checked){
          var self = this;
          var c = (self.catalog.channels[provId] || []).filter(function(x){ return x.id === channelId; })[0];
          var cell = document.querySelector('#catBody-' + provId + ' td[data-state="' + channelId.replace(/"/g, '\\"') + '"]');
          if(c && cell){ cell.innerHTML = self.providerState(provId, c, checked); }
      },
      renderProviderTable: function(id){
          var self = this;
          var body = document.getElementById('catBody-' + id);
          var all = self.catalog.channels[id] || [];
          var sel = self.catalog.sel[id] || (self.catalog.sel[id] = {});
          var q = (self.catalog.filter[id] || '').toLowerCase();
          var shown = all.filter(function(c){ return !q || (c.name||'').toLowerCase().indexOf(q) >= 0 || (c.group||'').toLowerCase().indexOf(q) >= 0; });
          var selected = Object.keys(sel).filter(function(k){ return sel[k]; }).length;
          var h = '<div class="jt-acc-tools"><div class="inputContainer"><input type="search" is="emby-input" data-act="filter" placeholder="Nombre o categoría" label="Buscar" value="'+self.esc(self.catalog.filter[id]||'')+'"/></div>' +
              '<button type="button" is="emby-button" class="raised emby-button jt-secondary" data-act="all"><span>Marcar visibles</span></button>' +
              '<button type="button" is="emby-button" class="raised emby-button jt-secondary" data-act="none"><span>Limpiar</span></button>' +
              '<button type="button" is="emby-button" class="raised emby-button jt-secondary" data-act="reload"><span>Recargar</span></button>' +
              '<button type="button" is="emby-button" class="raised button-submit emby-button" data-act="import"'+(selected ? '' : ' disabled')+'><span>'+t('Importar {0} canal(es)', selected)+'</span></button></div>' +
              '<div class="jt-muted jt-small">'+t('{0} de {1} canales', shown.length, all.length)+'</div>' +
              '<div class="jt-tableWrap"><table class="jt-table"><thead><tr><th></th><th>Logo</th><th>Canal</th><th>N.º</th><th>Categoría</th><th>Estado</th></tr></thead><tbody>';
          shown.forEach(function(c){
              var state = self.providerState(id, c, !!sel[c.id]);
              h += '<tr data-row="'+self.esc(c.id)+'"><td><input type="checkbox" data-id="'+self.esc(c.id)+'"'+(c.imported ? ' disabled' : '')+(sel[c.id] ? ' checked' : '')+' aria-label="'+t('Elegir {0}', self.esc(c.name))+'"/></td>' +
                  '<td>'+(c.logo ? '<img loading="lazy" alt="" src="'+self.esc(c.logo)+'"/>' : '')+'</td>' +
                  '<td title="'+self.esc(c.description||'')+'">'+self.esc(c.name)+'</td><td>'+(c.number || '')+'</td><td>'+self.esc(c.group||'')+'</td><td data-state="'+self.esc(c.id)+'">'+state+'</td></tr>';
          });
          h += '</tbody></table></div>';
          body.innerHTML = h;
          var filter = body.querySelector('[data-act="filter"]');
          filter.addEventListener('input', function(){
              self.catalog.filter[id] = filter.value;
              var pos = filter.selectionStart;
              self.renderProviderTable(id);
              var again = document.getElementById('catBody-' + id).querySelector('[data-act="filter"]');
              again.focus(); try { again.setSelectionRange(pos, pos); } catch(x){}
          });
          body.querySelectorAll('input[type="checkbox"][data-id]').forEach(function(cb){
              cb.addEventListener('change', function(){
                  sel[cb.dataset.id] = cb.checked;
                  self.refreshProviderState(id, cb.dataset.id, cb.checked);
                  var n = Object.keys(sel).filter(function(k){ return sel[k]; }).length;
                  var btn = body.querySelector('[data-act="import"]');
                  btn.disabled = !n;
                  btn.querySelector('span').textContent = t('Importar {0} canal(es)', n);
              });
          });
          body.querySelector('[data-act="all"]').addEventListener('click', function(){ shown.forEach(function(c){ if(!c.imported) sel[c.id] = true; }); self.renderProviderTable(id); });
          body.querySelector('[data-act="none"]').addEventListener('click', function(){ self.catalog.sel[id] = {}; self.renderProviderTable(id); });
          body.querySelector('[data-act="reload"]').addEventListener('click', function(){ self.loadProviderChannels(id, true); });
          body.querySelector('[data-act="import"]').addEventListener('click', function(){ self.importProvider(id); });
      },
      importProvider: function(id){
          var self = this;
          var ids = Object.keys(self.catalog.sel[id] || {}).filter(function(k){ return self.catalog.sel[id][k]; });
          if(!ids.length) return;
          // Un canal marcado que ya tienes (mismo nombre) se agrega como respaldo de ese; los demás se crean como canales nuevos.
          var byId = {};
          (self.catalog.channels[id] || []).forEach(function(c){ byId[c.id] = c; });
          var backupOf = {};
          ids.forEach(function(k){ if(byId[k] && byId[k].similarId){ backupOf[k] = byId[k].similarId; } });
          Dashboard.showLoadingMsg();
          ApiClient.ajax({ type:'POST', url:'/JellyTV/providers/' + encodeURIComponent(id) + '/import', contentType:'application/json', dataType:'json',
              data: JSON.stringify({ ChannelIds: ids, ExitCountry: document.getElementById('catExit').value || null, BackupOf: backupOf }) }).then(function(r){
              Dashboard.hideLoadingMsg();
              self.catalog.channels = {};
              self.reloadConfig(function(){
                  self.loadCatalog();
                  self.notify(t('{0} canal(es) importado(s)', r.imported) + (r.backedUp ? t(', {0} como respaldo de un canal que ya tenías', r.backedUp) : '') + (r.existing ? t(', {0} ya estaban', r.existing) : '') + '.' + (r.guideAdded ? ' ' + t('La guía del proveedor se añadió a las fuentes EPG: sincronízala para ver la programación.') : ''), (r.imported || r.backedUp) ? 'ok' : 'warn');
              });
          }, function(xhr){
              Dashboard.hideLoadingMsg();
              var msg = 'No se pudo importar.';
              try { var e = JSON.parse(xhr.responseText); if(e && e.error) msg = jtServerMsg(e.error); } catch(x){}
              self.notify(msg, 'err');
          });
      },

      switchTab: function(name){
          if(name === 'epg'){ this.loadEpgCoverage(); }
          if(name === 'catalog'){ this.loadCatalog(); }
          document.querySelectorAll('.jt-tab-btn').forEach(function(x){
              var on = x.dataset.tab === name;
              x.classList.toggle('active', on);
              x.setAttribute('aria-selected', on ? 'true' : 'false');
          });
          document.querySelectorAll('.jt-tab-content').forEach(function(x){
              x.classList.toggle('active', x.id === 'jt-tab-' + name);
          });
      },

      updateBanner: function(total, alive, dead, epg){
          var b = document.getElementById('jtStatusBanner');
          if(!b) return;
          var msg = '';
          var tone = '';
          if(!total){ msg = t('Aún no hay canales configurados. Empieza en la pestaña Canales.'); }
          else if(dead > 0){ msg = t('{0} canal(es) caído(s) de {1}. Revisa la columna Salud en Canales.', dead, total); tone = 'warn'; }
          else if(alive > 0){ msg = t('Todos los canales comprobados están vivos ({0}/{1}).', alive, total); tone = 'ok'; }
          else { msg = t('Hay {0} canal(es) sin comprobación de salud.', total); }
          if(total && !epg){ msg += ' ' + t('No hay fuentes EPG habilitadas.'); tone = tone || 'warn'; }
          b.className = 'jt-banner show ' + tone;
          b.textContent = msg;
      },

      notify: function(message, tone){
          var box = document.getElementById('jtToasts');
          if(!box){ return; }
          tone = tone || 'info';
          // Un mismo mensaje repetido (por ejemplo, copiar varias veces) no apila toasts.
          var existing = box.querySelectorAll('.jt-toast');
          for(var i = 0; i < existing.length; i++){
              if(existing[i].getAttribute('data-msg') === message){ existing[i].remove(); }
          }
          while(box.children.length >= 4){ box.removeChild(box.firstChild); }
          var icons = { ok: '\u2713', warn: '!', err: '\u00D7', info: 'i' };
          var el = document.createElement('div');
          el.className = 'jt-toast ' + tone;
          el.setAttribute('role', tone === 'err' ? 'alert' : 'status');
          el.setAttribute('data-msg', message);
          var ic = document.createElement('span');
          ic.className = 'jt-toast-ic';
          ic.setAttribute('aria-hidden', 'true');
          ic.textContent = icons[tone] || 'i';
          var tx = document.createElement('span');
          tx.className = 'jt-toast-tx';
          tx.textContent = message;
          var cl = document.createElement('button');
          cl.type = 'button';
          cl.className = 'jt-toast-x';
          cl.setAttribute('aria-label', 'Cerrar');
          cl.textContent = '\u00D7';
          el.appendChild(ic); el.appendChild(tx); el.appendChild(cl);
          var ttl = tone === 'err' ? 9000 : tone === 'warn' ? 7000 : 4500;
          var timer = null;
          var arm = function(){ timer = setTimeout(function(){ el.remove(); }, ttl); };
          el.addEventListener('mouseenter', function(){ clearTimeout(timer); });
          el.addEventListener('mouseleave', arm);
          cl.addEventListener('click', function(){ clearTimeout(timer); el.remove(); });
          box.appendChild(el);
          arm();
      },

      initDialog: function(){
          var dlg = document.getElementById('jtConfirm');
          if(!dlg || dlg.dataset.wired){ return; }
          dlg.dataset.wired = '1';
          document.getElementById('jtConfirmCancel').addEventListener('click', function(){ dlg.close(); });
          dlg.addEventListener('cancel', function(e){ e.preventDefault(); dlg.close(); });
      },

      confirm: function(title, text, onOk, okLabel){
          var dlg = document.getElementById('jtConfirm');
          if(!dlg){ if(window.confirm(text)){ onOk(); } return; }
          document.getElementById('jtConfirmTitle').textContent = title;
          document.getElementById('jtConfirmText').textContent = text;
          var ok = document.getElementById('jtConfirmOk');
          ok.querySelector('span').textContent = okLabel || 'Confirmar';
          // Eliminar/rotar son destructivos (rojo); el resto de confirmaciones usa el estilo primario.
          ok.className = 'raised emby-button ' + (/eliminar|rotar|borrar/i.test(okLabel || '') ? 'button-cancel' : 'button-submit');
          ok.onclick = function(){ ok.onclick = null; dlg.close(); onOk(); };
          dlg.showModal();
      },

      copy: function(text){
          var self = this;
          if(navigator.clipboard && navigator.clipboard.writeText){
              navigator.clipboard.writeText(text).then(
                  function(){ self.notify('Copiado al portapapeles.','ok'); },
                  function(){ self.notify('No se pudo copiar al portapapeles.','err'); });
          } else {
              self.notify(text,'ok');
          }
      },

      // Abrir la URL en otra pestaña daba 401: el navegador no manda el token de Jellyfin. Se pide con ApiClient y se muestra aquí.
      openDiag: function(kind){
          var out = document.getElementById('diagOut');
          if(!out) return;
          out.style.display = '';
          out.textContent = 'Cargando…';
          ApiClient.ajax({ type:'GET', url: ApiClient.getUrl(kind === 'log' ? 'JellyTV/log' : 'JellyTV/sessions'), dataType:'text' })
              .then(function(text){ out.textContent = text || '(vacío)'; if(kind === 'log'){ out.scrollTop = out.scrollHeight; } },
                    function(e){ out.textContent = t('No se pudo cargar (HTTP {0}).', (e && e.status) || '?'); });
      },

      relayKey: function(){
          return (this.config && this.config.RelayKey) || '';
      },

      // Base que se copia para clientes externos: la URL pública configurada o, si no hay, la desde la que abriste Jellyfin.
      publicBase: function(){
          var b = ((this.config && this.config.PublicStreamingBaseUrl) || '').trim().replace(/\/+$/, '');
          return b || window.location.origin;
      },

      tunerUrl: function(){
          var k = this.relayKey();
          return JellyTV.publicBase() + '/JellyTV/m3u' + (k ? '?key=' + encodeURIComponent(k) : '');
      },

      renderChannelTable: function(){
          var self = this;
          var all = this.config.Channels || [];
          // Opciones de categoría sin perder la selección actual.
          var selCat = document.getElementById('chCatFilter');
          var curCat = selCat ? selCat.value : '';
          if(selCat){
              var seen = {};
              all.forEach(function(c){ seen[(c.Category||'').trim()] = true; });
              var keys = Object.keys(seen).sort();
              var opts = '<option value="">Todas las categorias</option>';
              keys.forEach(function(k){ opts += '<option value="'+(k===''?'__none__':self.esc(k))+'">'+(k===''?'(sin categoria)':self.esc(k))+'</option>'; });
              selCat.innerHTML = opts;
              if(curCat && seen[curCat]){ selCat.value = curCat; } else { selCat.value = ''; }
          }
          var filterCat = selCat ? selCat.value : '';
          var q = (document.getElementById('chSearch') || {}).value || '';
          q = q.trim().toLowerCase();
          var ch = all.filter(function(c){
              var cat = (c.Category||'').trim();
              if(filterCat === '__none__'){ if(cat !== '') return false; }
              else if(filterCat !== '' && cat !== filterCat){ return false; }
              if(q){
                  var hay = ((c.Name||'')+' '+(c.Category||'')+' '+(c.TvgId||'')+' '+(c.TvgName||'')).toLowerCase();
                  if(hay.indexOf(q) === -1) return false;
              }
              return true;
          });
          var h = '';
          // Orden por columna activa.
          if(this.sortKey){
              var sk = this.sortKey, sd = this.sortDir;
              ch.sort(function(a,b){
                  var av = self.sortVal(a, sk), bv = self.sortVal(b, sk);
                  if(typeof av === 'number' && typeof bv === 'number'){ return (av - bv) * sd; }
                  return String(av).localeCompare(String(bv)) * sd;
              });
          }
          if(ch.length === 0){
              h = '<tr><td colspan="7" class="jt-empty">'+(all.length===0?t('No hay canales. Haz clic en <strong>+ Agregar canal</strong>.'):'Sin resultados para el filtro.')+'</td></tr>';
          } else {
              ch.forEach(function(c){
                  var types=['Directo','FAST','Tokenizado'];
                  var typeBadges=['jt-badge-green','jt-badge-blue','jt-badge-yellow'];
                  var health = c.LastCheckAlive===true?(!c.IsEnabled&&!c.AutoDisabled?'<span class="jt-badge jt-badge-green" title="Responde: ya puedes encenderlo">Vivo · ya puede encenderse</span>':'<span class="jt-badge jt-badge-green">Vivo</span>'):
                               c.LastCheckAlive===false?'<span class="jt-badge jt-badge-red">Caído</span>':
                               '<span class="jt-badge jt-badge-gray">—</span>';
                  var status = c.IsEnabled?'<span class="jt-badge jt-badge-green">ON</span>':'<span class="jt-badge jt-badge-gray">OFF</span>';
                  var initials = (c.Name||'?').split(/\s+/).filter(Boolean).slice(0,2).map(function(w){return w.charAt(0).toUpperCase();}).join('');
                  var logo = c.LogoUrl?'<img src="'+self.esc(c.LogoUrl)+'" alt="" onerror="this.outerHTML=\'<div class=jt-logo-ph>'+self.esc(initials)+'</div>\'"/>':'<div class="jt-logo-ph">'+self.esc(initials)+'</div>';
                  h += '<tr>';
                  h += '<td>'+logo+'</td>';
                  var srcCount = (c.Sources && c.Sources.length) || (c.Url ? 1 : 0);
                  var primaryType = (c.Sources && c.Sources.length && typeof c.Sources[0].Type === 'number') ? c.Sources[0].Type : (typeof c.Type === 'number' ? c.Type : 0);
                  var typeIndex = primaryType >= 0 && primaryType < types.length ? primaryType : 0;
                  h += '<td><strong>'+self.esc(c.Name)+'</strong>'+(srcCount>0?'<div class="jt-small jt-muted">'+t('{0} fuente(s)', srcCount)+'</div>':'')+'</td>';
                  h += '<td><span class="jt-badge '+typeBadges[typeIndex]+'">'+types[typeIndex]+'</span></td>';
                  h += '<td class="jt-small jt-muted">'+self.esc(c.Category||'—')+'</td>';
                  h += '<td>'+status+'</td>';
                  h += '<td>'+health+'</td>';
                  h += '<td><div class="jt-actions">';
                  h += '<button is="emby-button" type="button" class="raised emby-button jt-secondary" onclick="JellyTV.openChannelModal(\''+c.Id+'\')"><span>Editar</span></button>';
                  h += '<button is="emby-button" type="button" class="raised emby-button jt-secondary" onclick="JellyTV.toggleChannel(\''+c.Id+'\')"><span>'+(c.IsEnabled?'Off':'On')+'</span></button>';
                  h += '<button is="emby-button" type="button" class="raised button-cancel emby-button" onclick="JellyTV.deleteChannel(\''+c.Id+'\')"><span>Eliminar</span></button>';
                  h += '</div></td></tr>';
              });
          }
          document.getElementById('channelsTableBody').innerHTML = h;
          this.updateSortHeaders();
      },

      renderIdentityCard: function(){
          var id = this.modalIdentity;
          var logo = document.getElementById('chLogoUrl').value.trim();
          var img = document.getElementById('chIdLogo');
          var empty = document.getElementById('chIdLogoEmpty');
          if(logo){ img.src = logo; img.style.display = ''; empty.style.display = 'none'; img.onerror = function(){ img.style.display = 'none'; empty.style.display = ''; }; }
          else { img.style.display = 'none'; empty.style.display = ''; }
          document.getElementById('chIdName').textContent = id && id.Name ? id.Name : 'Sin identidad vinculada';
          var meta = [];
          if(id){
              if(id.Country) meta.push(id.Country);
              if(id.IptvOrgId) meta.push('iptv-org ' + id.IptvOrgId);
              if(id.Website) meta.push(id.Website);
              if(!id.Confirmed) meta.push(t('automática'));
          }
          document.getElementById('chIdMeta').textContent = meta.length ? meta.join(' · ') : 'Vincula el canal a su identidad (iptv-org) para completar el logo y enlazar la guía.';
          document.getElementById('btnIdClear').style.display = id ? '' : 'none';
      },
      searchIdentity: function(){
          var self = this;
          var name = document.getElementById('chName').value.trim();
          var box = document.getElementById('chIdResults');
          if(!name){ self.notify('Escribe primero el nombre del canal.','warn'); return; }
          box.className = 'jt-id-results open';
          box.innerHTML = '<div class="jt-id-empty">Buscando en iptv-org y en las guías locales…</div>';
          var country = self.modalIdentity && self.modalIdentity.Country ? '&country=' + encodeURIComponent(self.modalIdentity.Country) : '';
          var tvg = document.getElementById('chTvgId').value.trim(); if(tvg) country += '&tvgId=' + encodeURIComponent(tvg);
          ApiClient.ajax({ type:'GET', url:'/JellyTV/identity/search?q=' + encodeURIComponent(name) + country, dataType:'json' }).then(function(list){
              self._idCandidates = list || [];
              if(!self._idCandidates.length){ box.innerHTML = '<div class="jt-id-empty">Sin coincidencias. Puedes escribir el logo a mano.</div>'; return; }
              var h = '';
              self._idCandidates.forEach(function(c, i){
                  var meta = [c.Country, jtIdentitySource(c.Sources)].filter(Boolean).join(' · ');
                  h += '<div class="jt-id-item" data-i="'+i+'">' + (c.LogoUrl ? '<img src="'+self.esc(c.LogoUrl)+'" alt=""/>' : '<img alt="" style="visibility:hidden"/>') +
                       '<div class="jt-id-info"><div class="jt-id-name">'+self.esc(c.Name)+'</div><div class="jt-small jt-muted">'+self.esc(meta)+' · '+t('coincidencia {0}%', Math.round((c.Score||0)*100))+'</div></div></div>';
              });
              box.innerHTML = h;
              box.querySelectorAll('.jt-id-item').forEach(function(el){
                  el.addEventListener('click', function(){ self.pickIdentity(parseInt(el.dataset.i, 10)); });
              });
          }, function(){ box.innerHTML = '<div class="jt-id-empty">No se pudo validar la identidad.</div>'; });
      },
      pickIdentity: function(i){
          var c = (this._idCandidates || [])[i];
          if(!c) return;
          this.modalIdentity = { IptvOrgId: c.IptvOrgId || null, Name: c.Name, Country: c.Country || null, Website: c.Website || null, Confirmed: true };
          var logo = document.getElementById('chLogoUrl');
          if(c.LogoUrl && !logo.value.trim()) logo.value = c.LogoUrl;
          var tvg = document.getElementById('chTvgId');
          var guideId = c.TvgId || c.IptvOrgId;
          if(guideId && !tvg.value.trim()) tvg.value = guideId;
          var cs = document.getElementById('chCountry');
          var code = c.Country ? String(c.Country).toUpperCase() : '';
          if(cs && !cs.value && code && cs.querySelector('option[value="' + code + '"]')) cs.value = code;
          document.getElementById('chIdResults').classList.remove('open');
          this.renderIdentityCard();
      },
      enrichIdentities: function(){
          var self = this;
          self.confirm('Completar identidad y logos', 'Busca en iptv-org los canales sin identidad, país, tvg-id o logo y completa solo lo que falta, con coincidencias exactas. Los canales que ya confirmaste no se tocan.', function(){
              Dashboard.showLoadingMsg();
              ApiClient.ajax({ type:'POST', url:'/JellyTV/identity/enrich', dataType:'json' }).then(function(r){
                  Dashboard.hideLoadingMsg();
                  self.reloadConfig(function(){ self.notify(t('Identidad completada en {0} canal(es); {1} sin coincidencia segura.', r.updated, r.skipped), r.updated ? 'ok' : 'warn'); });
              }, function(){ Dashboard.hideLoadingMsg(); self.notify('No se pudo completar la identidad.','err'); });
          }, 'Completar');
      },
      openChannelModal: function(id){
          var ch = id ? this.config.Channels.find(function(c){return c.Id===id;}) : null;
          this.editingChannelId = id;
          var testOut = document.getElementById('chGuideTest');
          if(testOut){ testOut.innerHTML = ''; }
          document.getElementById('channelModalTitle').textContent = ch ? 'Editar canal' : 'Agregar canal';
          document.getElementById('chEditId').value = id || '';
          document.getElementById('chName').value = ch ? ch.Name : '';
          document.getElementById('chCategory').value = ch ? (ch.Category||'') : '';
          document.getElementById('chTvgId').value = ch ? (ch.TvgId||'') : '';
          document.getElementById('chCountry').value = ch ? (ch.Country||'') : '';
          document.getElementById('chEpgPage').value = ch ? (ch.EpgPageUrl||'') : '';
          document.getElementById('chLogoUrl').value = ch ? (ch.LogoUrl||'') : '';
          document.getElementById('chUserAgent').value = ch ? (ch.UserAgent||'') : '';
          document.getElementById('chHeaders').value = ch ? (ch.CustomHeaders||'') : '';
          document.getElementById('chEnabled').checked = ch ? ch.IsEnabled : true;
          this.modalIdentity = ch && ch.Identity ? JSON.parse(JSON.stringify(ch.Identity)) : null;
          document.getElementById('chIdResults').classList.remove('open');
          this.renderIdentityCard();
          var srcs = [];
          if(ch && ch.Sources && ch.Sources.length){
              srcs = ch.Sources.map(function(s){ return { Id:s.Id, Label:s.Label, IsAlive:s.IsAlive, LastError:s.LastError, LastCheckedAt:s.LastCheckedAt, Url:s.Url||'', Type:(typeof s.Type==='number'?s.Type:0), SessionToken:!!s.SessionToken, IsEnabled:s.IsEnabled!==false, ExitCountry:s.ExitCountry||'' }; });
          } else {
              srcs.push({ Url: ch ? (ch.Url||'') : '', Type: ch ? (typeof ch.Type==='number'?ch.Type:0) : 0, SessionToken: ch ? !!ch.SessionToken : false, IsEnabled:true });
              if(ch && ch.FallbackUrl1) srcs.push({ Url:ch.FallbackUrl1, Type: (typeof ch.Type==='number'?ch.Type:0), SessionToken:!!ch.SessionToken, IsEnabled:true });
              if(ch && ch.FallbackUrl2) srcs.push({ Url:ch.FallbackUrl2, Type: (typeof ch.Type==='number'?ch.Type:0), SessionToken:!!ch.SessionToken, IsEnabled:true });
          }
          if(srcs.length===0) srcs.push({ Url:'', Type:0, SessionToken:false, IsEnabled:true });
          this.modalSources = srcs;
          this.renderSources();
          document.getElementById('channelModal').classList.add('open');
      },
      renderSources: function(){
          var self = this;
          var box = document.getElementById('chSourcesList');
          if(!box) return;
          var srcs = this.modalSources || [];
          var h = '';
          for(var i=0;i<srcs.length;i++){
              var s = srcs[i];
              var isPrimary = i===0;
              h += '<div class="jt-src-card">';
              h += '<div class="jt-src-head">';
              h += '<span class="jt-src-title">'+(isPrimary?'Fuente principal':t('Respaldo {0}', i))+'</span>';
              if(!isPrimary){ h += '<button is="emby-button" type="button" class="raised button-cancel emby-button" style="padding:2px 8px;font-size:.72em;" onclick="JellyTV.removeSource('+i+')"><span>Quitar</span></button>'; }
              h += '</div>';
              h += '<div class="jt-small" id="srcHealth_'+i+'" style="margin-bottom:6px;">'+self.sourceHealthHtml(s)+' <button is="emby-button" type="button" class="raised emby-button jt-secondary" style="padding:2px 8px;font-size:.72em;" onclick="JellyTV.checkSourceNow('+i+')"><span>Comprobar ahora</span></button></div>';
              h += '<div class="inputContainer"><input id="srcUrl_'+i+'" is="emby-input" type="text" value="'+self.esc(s.Url)+'" placeholder="https://host/stream.m3u8" label="URL del stream *"/></div>';
              h += '<div class="jt-src-opts">';
              h += '<div class="inputContainer"><label class="inputLabel inputLabelUnfocused" for="srcType_'+i+'">Tipo</label><select id="srcType_'+i+'" is="emby-select" class="emby-select"><option value="0"'+(s.Type===0?' selected':'')+'>Directo</option><option value="1"'+(s.Type===1?' selected':'')+'>FAST</option><option value="2"'+(s.Type===2?' selected':'')+'>Tokenizado</option></select></div>';
              h += '<div class="checkboxContainer" id="srcSessionWrap_'+i+'" style="'+(s.Type===2?'':'display:none')+'"><input id="srcSession_'+i+'" type="checkbox"'+(s.SessionToken?' checked':'')+'/><label class="checkboxLabel emby-checkbox-label" for="srcSession_'+i+'">Token de sesión (dura horas)</label></div>';
              h += '<div class="checkboxContainer"><input id="srcOn_'+i+'" type="checkbox"'+(s.IsEnabled?' checked':'')+'/><label class="checkboxLabel emby-checkbox-label" for="srcOn_'+i+'">Habilitada</label></div>';
              h += '</div>';
              h += '<div class="jt-m-hint" id="srcHint_'+i+'" style="'+(s.Type===2?'':'display:none')+'"></div>';
              var countries = self.proxyCountries();
              var exit = (s.ExitCountry || '').toUpperCase();
              if(countries.length || exit){
                  var opts = '<option value="">Directa (este servidor)</option>';
                  countries.forEach(function(c){ opts += '<option value="'+c+'"'+(exit===c?' selected':'')+'>'+t('Por un proxy de {0}', c)+'</option>'; });
                  if(exit && countries.indexOf(exit) < 0){ opts += '<option value="'+self.esc(exit)+'" selected>'+t('{0} (no hay proxy habilitado)', self.esc(exit))+'</option>'; }
                  h += '<div id="srcExitWrap_'+i+'" style="'+(s.Type===2?'display:none':'')+'"><div class="inputContainer" style="margin-top:8px;max-width:420px;"><label class="inputLabel inputLabelUnfocused" for="srcExit_'+i+'">Salida</label><select id="srcExit_'+i+'" is="emby-select" class="emby-select">'+opts+'</select></div>';
                  h += '<div class="jt-m-hint">Para canales con geobloqueo: el servidor descarga todo el canal por un proxy de ese país. Los segmentos pasan por el proxy, así que consume su ancho de banda (una sola descarga por canal, sin importar cuántos lo vean).</div></div>';
              }
              h += '</div>';
          }
          box.innerHTML = h;
          for(var j=0;j<srcs.length;j++){
              (function(idx){
                  var sel = document.getElementById('srcType_'+idx);
                  var chk = document.getElementById('srcSession_'+idx);
                  var refresh = function(){
                      var tokenized = sel && sel.value === '2';
                      var exitWrap = document.getElementById('srcExitWrap_'+idx);
                      if(exitWrap) exitWrap.style.display = tokenized ? 'none' : '';
                      var wrap = document.getElementById('srcSessionWrap_'+idx);
                      if(wrap) wrap.style.display = tokenized ? '' : 'none';
                      var hint = document.getElementById('srcHint_'+idx);
                      if(!hint) return;
                      hint.style.display = tokenized ? '' : 'none';
                      hint.textContent = chk && chk.checked
                          ? 'Token de larga duración: se resuelve una vez, se comparte entre todos los clientes y se entrega directo al reproductor (Direct Play).'
                          : 'Token efímero (segundos o minutos): el plugin lo renueva por su cuenta y sirve el canal por su relay HLS, con una sola conexión al proveedor sin importar cuántos clientes lo vean.';
                  };
                  if(sel) sel.addEventListener('change', refresh);
                  if(chk) chk.addEventListener('change', refresh);
                  refresh();
              })(j);
          }
      },
      sourceHealthHtml: function(s){
          var badge = s.IsAlive === true ? '<span class="jt-badge jt-badge-green">Viva</span>'
              : s.IsAlive === false ? '<span class="jt-badge jt-badge-red">Caída</span>'
              : '<span class="jt-badge jt-badge-gray">Sin comprobar</span>';
          var when = s.LastCheckedAt ? ' <span class="jt-muted">'+new Date(s.LastCheckedAt).toLocaleString()+'</span>' : '';
          var err = (s.IsAlive === false && s.LastError) ? ' <span style="color:var(--jt-bad);">'+this.esc(s.LastError)+'</span>' : '';
          return badge+when+err;
      },
      checkSourceNow: function(i){
          var self = this;
          var s = (this.modalSources||[])[i];
          var urlEl = document.getElementById('srcUrl_'+i);
          var box = document.getElementById('srcHealth_'+i);
          if(!s || !urlEl || !urlEl.value.trim() || !box) return;
          var type = parseInt(document.getElementById('srcType_'+i).value)||0;
          var sess = !!document.getElementById('srcSession_'+i).checked;
          var exitEl = document.getElementById('srcExit_'+i);
          box.textContent = 'Comprobando…';
          this.quickCheck(urlEl.value.trim(), type, (exitEl && type !== 2) ? exitEl.value : null, sess).then(function(al){
              s.IsAlive = !!al; s.LastCheckedAt = new Date().toISOString(); s.LastError = al ? null : 'No respondió la comprobación';
              box.innerHTML = self.sourceHealthHtml(s)+' <button is="emby-button" type="button" class="raised emby-button jt-secondary" style="padding:2px 8px;font-size:.72em;" onclick="JellyTV.checkSourceNow('+i+')"><span>Comprobar ahora</span></button>';
          });
      },
      addSource: function(){
          if(!this.modalSources) this.modalSources = [];
          this.modalSources.push({ Url:'', Type:0, SessionToken:false, IsEnabled:true, ExitCountry:'' });
          this.renderSources();
      },
      removeSource: function(i){
          this.modalSources.splice(i,1);
          if(this.modalSources.length===0){ this.modalSources.push({ Url:'', Type:0, SessionToken:false, IsEnabled:true }); }
          this.renderSources();
      },
      closeChannelModal: function(){ document.getElementById('channelModal').classList.remove('open'); this.modalSources = null; },
      saveChannel: function(){
          var self = this;
          var name = document.getElementById('chName').value.trim();
          if(!name){ JellyTV.notify('El nombre es obligatorio.','warn'); return; }
          var srcs = [];
          for(var i=0;i<(this.modalSources||[]).length;i++){
              var urlEl = document.getElementById('srcUrl_'+i);
              if(!urlEl) continue;
              var url = urlEl.value.trim();
              if(!url) continue;
              var exitEl = document.getElementById('srcExit_'+i);
              var typeVal = parseInt(document.getElementById('srcType_'+i).value)||0;
              // Misma URL = misma fuente: conserva Id y salud (la fuente activa y el historial se enlazan por Id).
              var orig = this.modalSources[i];
              var same = !!(orig && orig.Id && orig.Url === url);
              srcs.push({ Id: same ? orig.Id : self._guid(), Label: same ? orig.Label : null, IsAlive: same ? orig.IsAlive : null, LastError: same ? orig.LastError : null, LastCheckedAt: same ? orig.LastCheckedAt : null, Url: url, Type: typeVal, SessionToken: !!document.getElementById('srcSession_'+i).checked, IsEnabled: !!document.getElementById('srcOn_'+i).checked, ExitCountry: (exitEl && typeVal !== 2 && exitEl.value) ? exitEl.value : null });
          }
          if(srcs.length===0){ JellyTV.notify('Agrega al menos una fuente con URL.','warn'); return; }
          var id = document.getElementById('chEditId').value;
          var ch = id ? this.config.Channels.find(function(c){return c.Id===id;}) : null;
          if(!ch){ ch = { Id: self._guid() }; this.config.Channels.push(ch); }
          ch.Name = name;
          ch.Category = document.getElementById('chCategory').value.trim() || null;
          ch.TvgId = document.getElementById('chTvgId').value.trim() || null;
          ch.Country = document.getElementById('chCountry').value || null;
          ch.EpgPageUrl = document.getElementById('chEpgPage').value.trim() || null;
          ch.LogoUrl = document.getElementById('chLogoUrl').value.trim() || null;
          ch.UserAgent = document.getElementById('chUserAgent').value.trim() || null;
          ch.CustomHeaders = document.getElementById('chHeaders').value.trim() || null;
          ch.Identity = this.modalIdentity || null;

          Dashboard.showLoadingMsg();
          // Verificar salud de las fuentes antes de guardar: si alguna responde, el canal
          // se guarda activo (ON); si ninguna responde, se guarda como OFF.
          var wantEnabled = document.getElementById('chEnabled').checked;
          var anyAlive = false;
          var checked = {};   // indice de fuente -> resultado de la comprobacion
          var seq = Promise.resolve(false);
          srcs.forEach(function(s, i){
              seq = seq.then(function(done){
                  if(done) return true;   // ya hay una viva: no seguimos comprobando
                  return self.quickCheck(s.Url, s.Type, s.ExitCountry, s.SessionToken).then(function(al){
                      checked[i] = !!al;
                      if(al){ anyAlive = true; return true; }
                      return false;
                  });
              });
          });
          var now = new Date().toISOString();
          seq.then(function(){
              // Reflejar el resultado en la tabla/dashboard: badge Vivo/Caido por fuente y por canal.
              srcs.forEach(function(s, i){
                  if(i in checked){
                      s.IsAlive = checked[i];
                      s.LastCheckedAt = now;
                  }
              });
              ch.IsEnabled = anyAlive ? wantEnabled : false;
              // Caído por chequeo (no por decisión del usuario): queda AutoDisabled para que el chequeo programado lo reactive al volver.
              ch.AutoDisabled = !anyAlive && wantEnabled;
              ch.LastCheckAlive = anyAlive;
              ch.LastCheckedAt = now;
              ch.Sources = srcs;
              ch.Url = srcs[0].Url;
              ch.Type = srcs[0].Type;
              ch.SessionToken = srcs[0].SessionToken;
              ch.FallbackUrl1 = srcs[1] ? srcs[1].Url : null;
              ch.FallbackUrl2 = srcs[2] ? srcs[2].Url : null;
              Dashboard.hideLoadingMsg();
              self._save(function(){ self.closeChannelModal(); self.renderAll(); });
              JellyTV.notify(anyAlive ? 'Canal guardado: VIVO y ACTIVO.' : 'Ninguna fuente respondió. Canal guardado como OFF (caído).', anyAlive ? 'ok' : 'warn');
          }).catch(function(){
              Dashboard.hideLoadingMsg();
              ch.IsEnabled = wantEnabled;
              self._save(function(){ self.closeChannelModal(); self.renderAll(); });
              JellyTV.notify('No se pudo verificar la salud; el canal se guardó con su estado.','warn');
          });
      },
      quickCheck: function(url, type, exit, sessionToken){
          return ApiClient.ajax({ type:'POST', url:'/JellyTV/check-source?url='+encodeURIComponent(url)+'&type='+((typeof type==='number'?type:0)||0)+(exit ? '&exit='+encodeURIComponent(exit) : '')+(sessionToken ? '&sessionToken=true' : ''), dataType:'json' })
              .then(function(r){ return !!(r && r.alive); })
              .catch(function(){ return false; });
      },
      toggleChannel: function(id){
          var ch = this.config.Channels.find(function(c){return c.Id===id;});
          if(ch){ ch.IsEnabled = !ch.IsEnabled; ch.AutoDisabled = false; this._save(function(){ this.renderAll(); }.bind(this)); }
      },
      deleteChannel: function(id){
          var self = this;
          this.confirm('Eliminar canal', 'Esto no se puede deshacer.', function(){
              self.config.Channels = self.config.Channels.filter(function(c){return c.Id!==id;});
              self._save(function(){ self.renderAll(); });
          }, 'Eliminar');
      },

      sortBy: function(key){
          if(this.sortKey === key){ this.sortDir = -this.sortDir; } else { this.sortKey = key; this.sortDir = 1; }
          this.renderChannelTable();
      },
      sortVal: function(c, key){
          if(key === 'name') return (c.Name||'').toLowerCase();
          if(key === 'cat') return (c.Category||'').toLowerCase();
          if(key === 'state') return c.IsEnabled ? 0 : 1;
          if(key === 'type'){ var t = (c.Sources && c.Sources.length && typeof c.Sources[0].Type === 'number') ? c.Sources[0].Type : (typeof c.Type === 'number' ? c.Type : 0); return t; }
          if(key === 'health'){ if(c.LastCheckAlive === true) return 0; if(c.LastCheckAlive === false) return 1; return 2; }
          return '';
      },
      updateSortHeaders: function(){
          document.querySelectorAll('th[data-sort] .jt-sort-ind').forEach(function(el){ el.textContent = ' ↕'; el.style.opacity = '0.3'; });
          if(this.sortKey){
              var ind = document.querySelector('th[data-sort="'+this.sortKey+'"] .jt-sort-ind');
              if(ind){ ind.style.opacity = '1'; ind.textContent = this.sortDir > 0 ? ' ▲' : ' ▼'; }
          }
      },

      // Países por los que sale al menos un proxy habilitado: son las opciones del selector «Salida» de cada fuente.
      proxyCountries: function(){
          var seen = {};
          (this.config.Proxies || []).forEach(function(p){
              var c = (p.Country || '').trim().toUpperCase();
              if(p.IsEnabled !== false && c.length === 2) seen[c] = true;
          });
          return Object.keys(seen).sort();
      },
      renderProxyTable: function(){
          var proxies = this.config.Proxies || [];
          var self = this;
          var h = '';
          if(proxies.length===0){
              h = '<tr><td colspan="6" class="jt-empty">No hay proxies configurados.</td></tr>';
          } else {
              proxies.forEach(function(p, i){
                  var on = p.IsEnabled !== false;
                  var country = (p.Country || '').trim().toUpperCase();
                  h += '<tr><td>'+self.esc(p.Host)+'</td><td>'+p.Port+'</td><td>'+(p.UseHttps?'HTTPS':'HTTP')+'</td>';
                  h += '<td>'+(country ? '<span class="jt-badge jt-badge-blue">'+self.esc(country)+'</span>' : '<span class="jt-badge jt-badge-yellow" title="Sin país no se puede elegir como salida">Sin país</span>')+'</td>';
                  h += '<td>'+(on ? '<span class="jt-badge jt-badge-green">Habilitado</span>' : '<span class="jt-badge jt-badge-gray">En pausa</span>')+'<div class="jt-muted jt-small" id="proxyRes_'+i+'"></div></td>';
                  h += '<td><div class="jt-actions">';
                  h += '<button is="emby-button" type="button" class="raised emby-button jt-secondary" onclick="JellyTV.testProxy('+i+')"><span>Probar</span></button>';
                  h += '<button is="emby-button" type="button" class="raised emby-button jt-secondary" onclick="JellyTV.toggleProxy('+i+')"><span>'+(on?'Pausar':'Activar')+'</span></button>';
                  h += '<button is="emby-button" type="button" class="raised button-cancel emby-button" onclick="JellyTV.deleteProxy('+i+')"><span>Eliminar</span></button>';
                  h += '</div></td></tr>';
              });
          }
          document.getElementById('proxiesTableBody').innerHTML = h;
      },
      // Dirección con la que se prueba un proxy: la que escribas o, si no, la de un canal que sale por su país.
      proxyTestTarget: function(country){
          var typed = (document.getElementById('proxyTestUrl').value || '').trim();
          if(typed) return typed;
          var found = '';
          (this.config.Channels || []).forEach(function(ch){
              (ch.Sources || []).forEach(function(s){
                  if(!found && s.IsEnabled !== false && s.Url && (s.ExitCountry || '').toUpperCase() === country) found = s.Url;
              });
          });
          return found;
      },
      runProxyTest: function(p, outEl){
          var self = this;
          var country = (p.Country || '').trim().toUpperCase();
          var url = this.proxyTestTarget(country);
          outEl.textContent = (url ? t('Probando desde el servidor con un canal real…') : t('Probando desde el servidor…'));
          return ApiClient.ajax({ type:'POST', url:'/JellyTV/proxies/test', contentType:'application/json', dataType:'json',
              data: JSON.stringify({ host:p.Host, port:p.Port, useHttps:!!p.UseHttps, username:p.Username||null, password:p.Password||null, url:url||null }) })
              .then(function(r){
                  outEl.textContent = (r.Ok ? '✓ ' : '✗ ') + r.Message + ' (' + r.Milliseconds + ' ms)';
                  outEl.style.color = r.Ok ? 'var(--jt-good)' : 'var(--jt-bad)';
                  return r;
              }, function(e){
                  var msg = e && e.responseJSON && e.responseJSON.error ? jtServerMsg(e.responseJSON.error) : 'No se pudo ejecutar la prueba.';
                  outEl.textContent = '✗ ' + msg;
                  outEl.style.color = 'var(--jt-bad)';
              });
      },
      testProxy: function(i){
          var p = (this.config.Proxies || [])[i];
          var out = document.getElementById('proxyRes_'+i);
          if(p && out) this.runProxyTest(p, out);
      },
      toggleProxy: function(i){
          var p = (this.config.Proxies || [])[i];
          if(!p) return;
          p.IsEnabled = p.IsEnabled === false;
          var self = this;
          this._save(function(){ self.renderAll(); });
      },
      deleteProxy: function(i){
          var self = this;
          var p = (this.config.Proxies || [])[i];
          if(!p) return;
          var inUse = (this.config.Channels || []).filter(function(ch){ return (ch.Sources || []).some(function(s){ return (s.ExitCountry || '').toUpperCase() === (p.Country || '').toUpperCase() && s.ExitCountry; }); }).length;
          var sameCountry = (this.config.Proxies || []).filter(function(q){ return q !== p && q.IsEnabled !== false && (q.Country || '').toUpperCase() === (p.Country || '').toUpperCase(); }).length;
          var warn = inUse > 0 && sameCountry === 0 ? t(' Es el único proxy de {0}: {1} canal(es) que salen por ese país quedarán sin salida.', (p.Country || '').toUpperCase(), inUse) : '';
          this.confirm('Eliminar proxy', t('{0}:{1} dejará de usarse.', p.Host, p.Port) + warn + t(' Esto no se puede deshacer.'), function(){
              self.config.Proxies.splice(i, 1);
              self._save(function(){ self.renderAll(); });
          }, 'Eliminar');
      },
      showProxyEditor: function(){
          ['proxyHost','proxyPort','proxyCountry','proxyUsername','proxyPassword','proxyBulk'].forEach(function(id){ document.getElementById(id).value = ''; });
          document.getElementById('proxyUseHttps').checked = true;
          document.getElementById('proxyEditorStatus').textContent = '';
          document.getElementById('proxyEditor').style.display = '';
          document.getElementById('proxyEditor').scrollIntoView({ behavior:'smooth', block:'nearest' });
      },
      // Lee el formulario: una lista pegada (una dirección por línea) o los campos sueltos.
      readProxyForm: function(){
          var country = (document.getElementById('proxyCountry').value || '').trim().toUpperCase();
          var user = document.getElementById('proxyUsername').value.trim() || null;
          var pass = document.getElementById('proxyPassword').value || null;
          var list = [];
          var bulk = (document.getElementById('proxyBulk').value || '').split(/\r?\n/).map(function(l){ return l.trim(); }).filter(Boolean);
          if(bulk.length){
              for(var i = 0; i < bulk.length; i++){
                  var m = /^(?:(https?):\/\/)?([^\s:\/]+):(\d{1,5})\/?$/i.exec(bulk[i]);
                  if(!m) return { error: t('No entiendo la línea «{0}». Usa el formato https://host:puerto.', bulk[i]) };
                  list.push({ Host: m[2], Port: parseInt(m[3], 10), UseHttps: (m[1] || '').toLowerCase() === 'https', Username: user, Password: pass, Country: country, IsEnabled: true });
              }
          } else {
              var host = document.getElementById('proxyHost').value.trim();
              var port = parseInt(document.getElementById('proxyPort').value, 10);
              if(!host || !port) return { error: 'Indica el host y el puerto.' };
              list.push({ Host: host, Port: port, UseHttps: document.getElementById('proxyUseHttps').checked, Username: user, Password: pass, Country: country, IsEnabled: true });
          }
          for(var j = 0; j < list.length; j++){
              if(list[j].Port < 1 || list[j].Port > 65535) return { error: t('El puerto de {0} no es válido.', list[j].Host) };
          }
          if(country.length !== 2) return { error: 'Indica el país de salida con dos letras (por ejemplo US). Es lo que permite elegir el proxy desde la fuente de un canal.' };
          return { list: list };
      },
      saveProxy: function(){
          var self = this;
          var st = document.getElementById('proxyEditorStatus');
          var form = this.readProxyForm();
          if(form.error){ st.textContent = form.error; st.style.color = 'var(--jt-bad)'; return; }
          var added = 0;
          form.list.forEach(function(p){
              var exists = (self.config.Proxies || []).some(function(q){ return q.Host === p.Host && q.Port === p.Port; });
              if(!exists){ p.Id = self._guid(); self.config.Proxies.push(p); added++; }
          });
          if(added === 0){ st.textContent = 'Esos proxies ya estaban en la lista.'; st.style.color = 'var(--jt-warn)'; return; }
          this._save(function(){
              document.getElementById('proxyEditor').style.display = 'none';
              self.notify(t('{0} proxy(s) agregado(s). Pulsa «Probar» para comprobar que llegan desde este servidor.', added), 'ok');
              self.renderAll();
          });
      },
      testProxyEditor: function(){
          var st = document.getElementById('proxyEditorStatus');
          var form = this.readProxyForm();
          if(form.error && !form.list){
              // Para probar basta con host y puerto: el país solo hace falta al guardar.
              var host = document.getElementById('proxyHost').value.trim();
              var port = parseInt(document.getElementById('proxyPort').value, 10);
              if(!host || !port){ st.textContent = form.error; st.style.color = 'var(--jt-bad)'; return; }
              form = { list: [{ Host: host, Port: port, UseHttps: document.getElementById('proxyUseHttps').checked, Username: document.getElementById('proxyUsername').value.trim() || null, Password: document.getElementById('proxyPassword').value || null, Country: (document.getElementById('proxyCountry').value || '').trim().toUpperCase() }] };
          }
          this.runProxyTest(form.list[0], st);
      },

      loadEpgCoverage: function(){
          var self = this;
          var sum = document.getElementById('epgCoverageSummary');
          ApiClient.getJSON(ApiClient.getUrl('JellyTV/epg/coverage')).then(function(d){ self.renderEpgCoverage(d); }, function(){
              if(sum) sum.textContent = 'No se pudo leer la cobertura de la guía.';
          });
      },
      renderEpgCoverage: function(d){
          var sum = document.getElementById('epgCoverageSummary');
          var body = document.getElementById('epgCoverageBody');
          if(!sum || !body){ return; }
          if(!d.guideExists){
              sum.textContent = 'Todavía no hay guía local. Sincroniza una fuente para generarla.';
              body.innerHTML = '';
              return;
          }
          var age = d.guideAgeHours;
          var ageText = age < 1 ? t('hace menos de 1 h') : age < 48 ? t('hace {0} h', Math.round(age)) : t('hace {0} d', Math.round(age / 24));
          var parts = [t('{0} de {1} canales habilitados con guía', d.withGuide, d.enabledChannels)];
          if(d.short_){ parts.push(t('{0} por vencer', d.short_)); }
          if(d.withoutGuide){ parts.push(t('{0} sin guía', d.withoutGuide)); }
          parts.push(t('guía actualizada {0}', ageText));
          sum.textContent = parts.join(' · ');
          var stale = age > 48;
          sum.style.color = (d.withoutGuide || d.short_ || stale) ? 'var(--jt-warn)' : '';
          var labels = { ok: ['jt-badge-green','Con guía'], short: ['jt-badge-yellow','Por vencer'], none: ['jt-badge-red','Sin guía'], disabled: ['jt-badge-gray','Canal apagado'] };
          var h = '';
          (d.channels || []).forEach(function(c){
              var l = labels[c.state] || labels.none;
              var until = c.until ? new Date(c.until).toLocaleString() : '—';
              var src = c.source ? JellyTV.esc(c.source) + (c.alternates ? ' <span class="jt-muted">' + t('+{0} de respaldo', c.alternates) + '</span>' : '') : '<span class="jt-muted">—</span>';
              h += '<tr><td><strong>' + JellyTV.esc(c.name) + '</strong>' + (c.country ? ' <span class="jt-muted">' + JellyTV.esc(c.country) + '</span>' : '') + '</td><td class="jt-muted">' + JellyTV.esc(c.tvgId || '—') + '</td><td>' + src + '</td><td>' + c.programmes + '</td><td data-v="' + (c.until ? new Date(c.until).getTime() : 0) + '">' + until + '</td><td><span class="jt-badge ' + l[0] + '">' + l[1] + '</span></td></tr>';
          });
          body.innerHTML = h;
      },
      renderEpgList: function(){
          var epg = this.config.EpgSources || [];
          var disp = document.getElementById('epgUrlDisplay');
          if(disp) disp.textContent = JellyTV.publicBase() + '/JellyTV/epg.xml';
          var h = '';
          if(epg.length===0){
              h = '<p class="jt-muted jt-small" style="margin-top:1em;">' + t('Sin fuentes EPG. Haz clic en <strong>+ Agregar fuente EPG</strong>.') + '</p>';
          } else {
              epg.forEach(function(s){
                  var state = s.IsEnabled ? '<span class="jt-badge jt-badge-green">ON</span>' : '<span class="jt-badge jt-badge-gray">OFF</span>';
                  var sum = s.LastSyncSummary ? '<div class="jt-small jt-muted" style="margin-top:2px;">'+JellyTV.esc(s.LastSyncSummary)+'</div>' : '';
                  var err = s.LastSyncError ? '<div class="jt-small" style="color:var(--jt-bad);margin-top:2px;">'+t('Error: {0}', JellyTV.esc(s.LastSyncError))+'</div>' : '';
                  h += '<div class="jt-list-row">';
                  h += '<div style="min-width:0;"><strong>'+JellyTV.esc(s.Name)+'</strong> <span class="jt-muted jt-small">'+JellyTV.esc(s.Url||'')+'</span>';
                  h += '<div style="margin-top:3px;">'+state+'</div>'+sum+err;
                  h += '</div><div class="jt-actions">';
                  h += '<button is="emby-button" type="button" class="raised emby-button jt-secondary" onclick="JellyTV.openEpgEditor(\''+s.Id+'\')"><span>Editar</span></button>';
                  h += '<button is="emby-button" type="button" class="raised button-cancel emby-button" onclick="JellyTV.deleteEpg(\''+s.Id+'\')"><span>Eliminar</span></button>';
                  h += '</div></div>';
              });
          }
          document.getElementById('epgSourceList').innerHTML = h;
      },
      openEpgEditor: function(id){
          var s = id ? (this.config.EpgSources||[]).find(function(x){return x.Id===id;}) : null;
          document.getElementById('epgEditId').value = s ? s.Id : '';
          document.getElementById('epgEditName').value = s ? (s.Name||'') : '';
          document.getElementById('epgEditUrl').value = s ? (s.Url||'') : '';
          document.getElementById('epgEditEnabled').checked = s ? s.IsEnabled !== false : true;
          var editor = document.getElementById('epgEditor');
          editor.style.display = 'block';
          // El editor se abre junto a la lista de fuentes: se lleva a la vista y se deja el cursor en el primer campo.
          editor.scrollIntoView({ behavior: 'smooth', block: 'center' });
          var first = document.getElementById('epgEditName');
          if(first){ setTimeout(function(){ first.focus(); }, 250); }
      },
      cancelEpgEditor: function(){
          document.getElementById('epgEditor').style.display = 'none';
      },
      saveEpgSource: function(){
          var self = this;
          var name = document.getElementById('epgEditName').value.trim();
          var url = document.getElementById('epgEditUrl').value.trim();
          if(!name || !url){ JellyTV.notify('Nombre y URL son obligatorios.','warn'); return; }
          var id = document.getElementById('epgEditId').value;
          this.config.EpgSources = this.config.EpgSources || [];
          var s = id ? this.config.EpgSources.find(function(x){return x.Id===id;}) : null;
          if(!s){ s = { Id: self._guid() }; this.config.EpgSources.push(s); }
          s.Name = name;
          s.Url = url;
          s.IsEnabled = document.getElementById('epgEditEnabled').checked;
          this.cancelEpgEditor();
          this._save(function(){ self.renderAll(); });
          JellyTV.notify('Fuente EPG guardada. Ejecuta "Sincronizar guía local" para aplicarla.','ok');
      },
      deleteEpg: function(id){
          var self = this;
          this.confirm('Eliminar fuente EPG', 'La gu\u00EDa dejar\u00E1 de incluir esta fuente en la pr\u00F3xima sincronizaci\u00F3n. Esto no se puede deshacer.', function(){
              self.config.EpgSources = self.config.EpgSources.filter(function(s){return s.Id!==id;});
              self._save(function(){ self.renderAll(); });
          }, 'Eliminar');
      },
      copyEpgUrl: function(){
          var url = JellyTV.publicBase() + '/JellyTV/epg.xml';
          JellyTV.copy(url);
      },
      syncEpg: function(){
          var self = this;
          this.confirm('Sincronizar la gu\u00EDa local', 'Descarga todas las fuentes EPG habilitadas y reconstruye la gu\u00EDa. Puede tardar varios minutos y se ejecuta en segundo plano.', function(){ self._syncEpgNow(); }, 'Sincronizar');
      },
      testGuide: function(){
          var url = document.getElementById('chEpgPage').value.trim();
          var out = document.getElementById('chGuideTest');
          if(!url){ JellyTV.notify('Escribe primero la dirección de la guía.','warn'); return; }
          out.textContent = 'Comprobando\u2026 (puede tardar unos segundos)';
          ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('JellyTV/epg/test-guide'), data: JSON.stringify({ url: url, name: document.getElementById('chName').value.trim() }), contentType: 'application/json', dataType: 'json' }).then(function(r){
              if(r.error){ out.innerHTML = '<span style="color:var(--jt-bad)">' + JellyTV.esc(r.error) + '</span>' + (r.format && r.format !== '\u2014' ? ' <span class="jt-muted">(' + JellyTV.esc(r.format) + ')</span>' : ''); return; }
              var rows = (r.items || []).map(function(i){ return '<tr><td>' + new Date(i.start).toLocaleString() + '</td><td>' + JellyTV.esc(i.title) + '</td></tr>'; }).join('');
              out.innerHTML = '<span style="color:var(--jt-good)">' + t('\u2713 {0} programas reconocidos', r.count) + '</span> <span class="jt-muted">\u00B7 ' + JellyTV.esc(r.format) + '</span><table class="jt-table" style="min-width:0;margin-top:6px"><tbody>' + rows + '</tbody></table>';
          }, function(e){
              var why = e && e.status ? t('el servidor respondió HTTP {0}', e.status) : (e && e.message ? e.message : t('no hubo respuesta del servidor (tiempo agotado o conexión cortada)'));
              out.innerHTML = '<span style="color:var(--jt-bad)">' + t('No se pudo comprobar: {0}.', JellyTV.esc(why)) + '</span>';
          });
      },
      syncEpgClean: function(){
          var self = this;
          this.confirm('Sincronizar desde cero', 'Borra las copias guardadas de cada fuente y lo que el plugin recuerda de sincronizaciones anteriores, y vuelve a buscar todos los metadatos y las im\u00E1genes. Tarda m\u00E1s que una sincronizaci\u00F3n normal. La gu\u00EDa actual se mantiene hasta que termine bien.', function(){ self._syncEpgNow(true); }, 'Sincronizar');
      },
      _syncEpgNow: function(clean){
          var self = this;
          var anySource = (this.config.EpgSources||[]).some(function(s){ return s.IsEnabled && s.Url; });
          var anyOwn = (this.config.Channels||[]).some(function(c){ return c.IsEnabled && c.EpgPageUrl; });
          var auto = this.config.AutoGuideEnabled !== false;
          if(!anySource && !anyOwn && !auto){ JellyTV.notify('No hay fuentes EPG habilitadas ni canales con guía propia.','warn'); return; }
          ApiClient.ajax({type:'POST',url:'/JellyTV/epg/sync' + (clean === true ? '?clean=true' : '')}).then(function(){
              self.trackEpg();
          }, function(e){
              if(e && e.status === 409){ JellyTV.notify('Ya hay una sincronización en curso.','warn'); self.trackEpg(); return; }
              JellyTV.notify(t('Error al iniciar la sincronización (HTTP {0}).', e && e.status || '?'),'err');
          });
      },
      reloadConfig: function(cb){
          var self = this;
          ApiClient.getPluginConfiguration(this.pluginId).then(function(cfg){
              self.config = cfg;
              self.config.Channels = self.config.Channels || [];
              self.config.Proxies = self.config.Proxies || [];
              self.config.EpgSources = self.config.EpgSources || [];
              self.renderAll();
              if(cb) cb();
          }).catch(function(){});
      },

      renderSettings: function(){
          var c = this.config;
          document.getElementById('UserAgent').value = c.UserAgent || '';
          document.getElementById('PublicStreamingBaseUrl').value = c.PublicStreamingBaseUrl || '';
          document.getElementById('TmdbApiKey').value = c.TmdbApiKey || '';
          document.getElementById('EpgTimezone').value = c.EpgTimezone || '';
          document.getElementById('HtmlGuideRecipesJson').value = c.HtmlGuideRecipesJson || '';
          document.getElementById('SourceSyncIntervalHours').value = c.SourceSyncIntervalHours || 24;
          document.getElementById('HealthCheckIntervalMinutes').value = c.HealthCheckIntervalMinutes || 30;
          document.getElementById('AutoRefreshGuideOnChange').checked = c.AutoRefreshGuideOnChange !== false;
          document.getElementById('AutoGuideEnabled').checked = c.AutoGuideEnabled !== false;
          document.getElementById('PreferredAudioLanguages').value = c.PreferredAudioLanguages || '';
          document.getElementById('PreferredSubtitleLanguages').value = c.PreferredSubtitleLanguages || '';
          document.getElementById('LiveTvMode').value = c.LiveTvMode || 'Auto';
          this.updateLiveTvHint();
          document.getElementById('VerboseLogging').checked = c.VerboseLogging === true;
          document.getElementById('RelayKey').value = c.RelayKey || '';
          document.getElementById('StorageBackend').value = c.StorageBackend || 'Auto';
          document.getElementById('JsonDataPath').value = c.JsonDataPath || '';
          document.getElementById('EnableJsonCourtesyBackup').checked = c.EnableJsonCourtesyBackup === true;
          this.loadStorageInfo();
          document.getElementById('tunerUrlDisplay').textContent = this.tunerUrl();
          var ov = document.getElementById('ovTunerUrl');
          if(ov) ov.textContent = this.tunerUrl();
          var ovE = document.getElementById('ovEpgUrl');
          if(ovE) ovE.textContent = JellyTV.publicBase() + '/JellyTV/epg.xml';
      },
      saveSettings: function(){
          var c = this.config;
          c.UserAgent = document.getElementById('UserAgent').value;
          c.PublicStreamingBaseUrl = document.getElementById('PublicStreamingBaseUrl').value.trim();
          c.TmdbApiKey = document.getElementById('TmdbApiKey').value.trim();
          c.EpgTimezone = document.getElementById('EpgTimezone').value.trim();
          var recipes = document.getElementById('HtmlGuideRecipesJson').value.trim();
          if(recipes){
              try { if(!Array.isArray(JSON.parse(recipes))){ throw new Error('no es una lista'); } }
              catch(e){ JellyTV.notify('Las recetas HTML no son una lista JSON válida: ' + e.message, 'err'); return; }
          }
          c.HtmlGuideRecipesJson = recipes || null;
          c.SourceSyncIntervalHours = parseInt(document.getElementById('SourceSyncIntervalHours').value)||24;
          c.HealthCheckIntervalMinutes = parseInt(document.getElementById('HealthCheckIntervalMinutes').value)||30;
          c.AutoRefreshGuideOnChange = document.getElementById('AutoRefreshGuideOnChange').checked;
          c.AutoGuideEnabled = document.getElementById('AutoGuideEnabled').checked;
          c.PreferredAudioLanguages = document.getElementById('PreferredAudioLanguages').value;
          c.PreferredSubtitleLanguages = document.getElementById('PreferredSubtitleLanguages').value;
          c.LiveTvMode = document.getElementById('LiveTvMode').value;
          c.VerboseLogging = document.getElementById('VerboseLogging').checked;
          c.StorageBackend = document.getElementById('StorageBackend').value;
          c.JsonDataPath = document.getElementById('JsonDataPath').value.trim();
          c.EnableJsonCourtesyBackup = document.getElementById('EnableJsonCourtesyBackup').checked;
          this._save(function(){ JellyTV.notify('Configuración guardada.','ok'); });
      },
      loadStorageInfo: function(){          var el = document.getElementById('storageInfo');
          var filesEl = document.getElementById('storageFiles');
          if(!el) return;
          ApiClient.ajax({type:'GET', url:'/JellyTV/storage', dataType:'json'}).then(function(info){
              var label = info.backend === 'postgres' ? 'PostgreSQL' : 'Archivos JSON';
              el.textContent = label + ' — ' + (info.description || '');
              if(filesEl){
                  var files = info.jsonFiles || [];
                  var detail = 'Carpeta JSON: ' + (info.jsonFolder || '—');
                  if(info.jsonBackup) detail += ' · copia activa';
                  detail += files.length ? ' · ' + files.join(', ') : ' · sin archivos aún';
                  filesEl.textContent = detail;
              }
          }, function(){
              el.textContent = 'No se pudo consultar el backend activo.';
              if(filesEl) filesEl.textContent = '';
          });
      },
      updateLiveTvHint: function(){
          var mode = document.getElementById('LiveTvMode') ? document.getElementById('LiveTvMode').value : 'Auto';
          var hint = document.getElementById('liveTvHint');
          var row = document.getElementById('tunerUrlRow');
          if(row) row.style.display = mode === 'M3u' ? '' : 'none';
          if(!hint) return;
          if(mode === 'Native'){
              hint.innerHTML = 'Modo nativo: no agregues ninguna playlist en Live TV. Jellyfin toma los canales y la guía del plugin. Si ya tienes el tuner M3U de JellyTV registrado, elimínalo para no duplicar canales.';
          } else if(mode === 'M3u'){
              hint.innerHTML = 'Modo heredado: agrega esta URL en <strong>Live TV → Agregar sintonizador → M3U Tuner</strong> y <code>/JellyTV/epg.xml</code> como XMLTV. Jellyfin no reproduce los .m3u8 de un tuner M3U en directo, así que los transcodifica.';
          } else {
              hint.innerHTML = 'Automático: Jellyfin toma canales y guía del servicio nativo del plugin y se retira de Live TV el tuner M3U de JellyTV si existía. No tienes que registrar nada.';
          }
      },
      useMyTimezone: function(){
          try {
              var zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
              var el = document.getElementById('EpgTimezone');
              if(zone && el){ el.value = zone; JellyTV.notify('Zona detectada: ' + zone + '. Guarda la configuración para aplicarla.','ok'); }
          } catch(e){ JellyTV.notify('No se pudo detectar la zona del navegador.','err'); }
      },

      // Exporta/importa un M3U estándar: solo tvg-id, tvg-name, tvg-logo, group-title y la URL de la fuente
      // principal, sin relay, sin base64 ni claves del plugin. Sirve para respaldar los canales o moverlos a/desde
      // otro reproductor; no reemplaza al tuner.m3u (ese sí pasa por el relay y lo arma el servidor).
      exportM3u: function(){
          var lines = ['#EXTM3U'];
          var count = 0;
          (this.config.Channels || []).filter(function(c){ return c.IsEnabled !== false; }).forEach(function(ch){
              var src = (ch.Sources || []).filter(function(s){ return s.IsEnabled !== false && s.Url; })[0];
              var url = src ? src.Url : (ch.Url || '');
              if(!url) return;
              function attr(name, value){
                  if(!value) return '';
                  return ' ' + name + '="' + String(value).replace(/"/g,'&quot;').replace(/[\r\n,]/g,' ') + '"';
              }
              var attrs = attr('tvg-id', ch.TvgId) + attr('tvg-name', ch.TvgName || ch.Name) + attr('tvg-logo', ch.LogoUrl) + attr('group-title', ch.Category);
              var name = (ch.Name || '').replace(/[\r\n,]/g,' ');
              lines.push('#EXTINF:-1' + attrs + ',' + name);
              lines.push(url);
              count++;
          });
          if(count === 0){ JellyTV.notify('No hay canales con una fuente para exportar.','warn'); return; }
          var blob = new Blob([lines.join('\n') + '\n'], { type: 'audio/x-mpegurl' });
          var a = document.createElement('a');
          a.href = URL.createObjectURL(blob);
          a.download = 'jellytv-canales.m3u';
          document.body.appendChild(a); a.click(); document.body.removeChild(a);
          setTimeout(function(){ URL.revokeObjectURL(a.href); }, 2000);
          JellyTV.notify(count + ' canal(es) exportados.', 'ok');
      },
      parseM3u: function(text){
          var lines = text.split(/\r\n|\r|\n/);
          var items = [];
          var pending = null;
          for(var i=0;i<lines.length;i++){
              var line = lines[i].trim();
              if(!line) continue;
              if(line.toUpperCase().indexOf('#EXTM3U') === 0) continue;
              if(line.indexOf('#EXTINF') === 0){
                  var rest = line.substring(line.indexOf(':')+1);
                  var comma = rest.lastIndexOf(',');
                  var attrPart = comma >= 0 ? rest.substring(0, comma) : rest;
                  var name = comma >= 0 ? rest.substring(comma+1).trim() : '';
                  var attrs = {};
                  var re = /([a-zA-Z0-9_-]+)="([^"]*)"/g, m;
                  while((m = re.exec(attrPart))) attrs[m[1].toLowerCase()] = m[2];
                  pending = { name: name, attrs: attrs };
                  continue;
              }
              if(line.indexOf('#') === 0) continue; // otras directivas (#EXTVLCOPT, #EXTGRP, etc.) se ignoran
              if(pending){
                  items.push({
                      name: pending.name || line,
                      url: line,
                      tvgId: pending.attrs['tvg-id'] || null,
                      tvgName: pending.attrs['tvg-name'] || null,
                      logo: pending.attrs['tvg-logo'] || null,
                      category: pending.attrs['group-title'] || null
                  });
                  pending = null;
              }
          }
          return items;
      },
      importM3u: function(text){
          var self = this;
          var items = this.parseM3u(text);
          if(items.length === 0){ JellyTV.notify('El archivo no tiene canales reconocibles (formato M3U estándar: #EXTINF seguido de una URL).','warn'); return; }
          var existingUrls = {};
          (this.config.Channels || []).forEach(function(ch){
              (ch.Sources || []).forEach(function(s){ if(s.Url) existingUrls[s.Url] = true; });
              if(ch.Url) existingUrls[ch.Url] = true;
          });
          var toAdd = items.filter(function(it){ return !existingUrls[it.url]; });
          var skipped = items.length - toAdd.length;
          if(toAdd.length === 0){ JellyTV.notify('Los ' + items.length + ' canal(es) del archivo ya existen (misma URL de fuente).','warn'); return; }
          var msg = 'Se agregarán ' + toAdd.length + ' canal(es) nuevo(s)' + (skipped ? ' (' + skipped + ' ya existían por URL y se omiten)' : '') + ', como canales Directo y deshabilitados hasta que verifiques cada fuente.';
          this.confirm('Importar M3U', msg, function(){
              toAdd.forEach(function(it){
                  self.config.Channels.push({
                      Id: self._guid(),
                      Name: it.name || 'Sin nombre',
                      Category: it.category || null,
                      TvgId: it.tvgId || null,
                      TvgName: it.tvgName || null,
                      LogoUrl: it.logo || null,
                      Country: null,
                      IsEnabled: false,
                      Type: 0,
                      Sources: [{ Id: self._guid(), Label: 'Principal', Url: it.url, Type: 0, SessionToken: false, IsEnabled: true }]
                  });
              });
              Dashboard.showLoadingMsg();
              self._save(function(){
                  Dashboard.hideLoadingMsg();
                  JellyTV.notify(toAdd.length + ' canal(es) importados, deshabilitados. Verifica cada fuente y actívalos desde la tabla.', 'ok');
              });
          }, 'Importar');
      },
      _save: function(cb){
          var self = this;
          ApiClient.ajax({ type:'POST', url:'/JellyTV/config', data: JSON.stringify(this.config), contentType:'application/json', dataType:'json' })
              .then(function(){ return ApiClient.getPluginConfiguration(self.pluginId); })
              .then(function(cfg){
                  self.config = cfg;
                  self.config.Channels = self.config.Channels || [];
                  self.config.Proxies = self.config.Proxies || [];
                  self.config.EpgSources = self.config.EpgSources || [];
                  self.renderAll();
                  // Trigger Jellyfin guide refresh so new channels appear immediately.
                  ApiClient.ajax({type:'POST',url:'/JellyTV/refresh-guide'});
                  if(cb) cb();
              })
              .catch(function(e){
                  Dashboard.hideLoadingMsg();
                  JellyTV.notify('Error al guardar la configuración (HTTP '+(e && e.status || '?')+'). Revisa el log del plugin.','err');
              });
      },
      _guid: function(){ return 'xxxxxxxxxxxx'.replace(/x/g,function(){return(Math.random()*16|0).toString(16);}); },
      esc: function(s){ return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }
  };
  JellyTV.init();
