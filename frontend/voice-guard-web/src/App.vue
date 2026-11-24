<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue'

const api = (import.meta.env.VITE_API_BASE_URL ?? '/api').replace(/\/$/, '')
const storageKey = 'voiceguard.history.v1'
const selected = ref(null)
const preview = ref('')
const fileInput = ref(null)
const result = ref(null)
const error = ref('')
const historyError = ref('')
const busy = ref(false)
const status = ref('Comprobando conexión')
const filter = ref('todos')
const confirmClear = ref(false)
const history = ref([])

const synthetic = computed(() => history.value.filter(row => row.resultado === 'sintetica').length)
const flaggedRate = computed(() => history.value.length ? Math.round(synthetic.value / history.value.length * 100) : 0)
const filtered = computed(() => filter.value === 'todos' ? history.value : history.value.filter(row => row.resultado === filter.value))
const date = new Intl.DateTimeFormat('es-PE', { dateStyle: 'medium', timeStyle: 'short' })
const label = value => value === 'sintetica' ? 'Posible voz sintética' : 'Probable voz real'
const percent = value => (value * 100).toFixed(0) + '%'

onMounted(() => {
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? '[]')
    if (Array.isArray(saved)) {
      history.value = saved.filter(row => row && typeof row.archivo === 'string'
        && Number.isFinite(Date.parse(row.fecha)) && ['real', 'sintetica'].includes(row.resultado)
        && Number.isFinite(row.confianza) && row.confianza >= 0 && row.confianza <= 1).slice(0, 100)
    }
  } catch {
    historyError.value = 'No se pudo leer el historial de este navegador.'
  }
  checkStatus()
})
onUnmounted(() => URL.revokeObjectURL(preview.value))

async function checkStatus() {
  try {
    const response = await fetch(api + '/estado', { signal: AbortSignal.timeout(5000) })
    if (!response.ok) throw new Error()
    const { modeloDisponible } = await response.json()
    status.value = modeloDisponible ? 'Modelo disponible' : 'Modelo sin cargar'
  } catch {
    status.value = 'API sin conexión'
  }
}

function choose(file) {
  if (busy.value || !file) return
  resetSelection()
  if (!file.name.toLowerCase().endsWith('.wav') || !file.size || file.size > 1024 * 1024) {
    error.value = 'Selecciona un WAV de hasta 1 MB que contenga audio.'
    return
  }
  selected.value = file
  preview.value = URL.createObjectURL(file)
}

function resetSelection() {
  URL.revokeObjectURL(preview.value)
  preview.value = ''
  selected.value = null
  result.value = null
  error.value = ''
  if (fileInput.value) fileInput.value.value = ''
}

function saveHistory(records) {
  history.value = records
  try {
    localStorage.setItem(storageKey, JSON.stringify(records))
    historyError.value = ''
  } catch {
    historyError.value = 'El navegador no pudo guardar los cambios en el historial.'
  }
}

async function analyze() {
  if (!selected.value || busy.value) return
  busy.value = true
  result.value = null
  error.value = ''
  const form = new FormData()
  form.append('audio', selected.value)
  try {
    const response = await fetch(api + '/analisis', {
      method: 'POST', body: form, signal: AbortSignal.timeout(15000),
    })
    const data = await response.json().catch(() => ({}))
    if (!response.ok) throw new Error(data.error ?? 'El servicio devolvió HTTP ' + response.status + '.')
    if (!['real', 'sintetica'].includes(data.resultado) || !Number.isFinite(data.confianza)
        || data.confianza < 0 || data.confianza > 1) {
      throw new Error('La respuesta del servidor no contiene un diagnóstico válido.')
    }
    const record = { id: globalThis.crypto?.randomUUID?.() ?? String(Date.now()), archivo: selected.value.name,
      fecha: new Date().toISOString(), resultado: data.resultado, confianza: data.confianza }
    result.value = record
    status.value = 'Modelo disponible'
    saveHistory([record, ...history.value].slice(0, 100))
  } catch (failure) {
    error.value = failure.name === 'TimeoutError'
      ? 'El análisis excedió el tiempo de espera. Inténtalo de nuevo.'
      : failure instanceof TypeError ? 'No se pudo conectar con la API. Comprueba el servicio y reintenta.' : failure.message
    await checkStatus()
  } finally {
    busy.value = false
  }
}

function exportHistory() {
  const url = URL.createObjectURL(new Blob([JSON.stringify(history.value, null, 2)], { type: 'application/json' }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = 'voiceguard-historial.json'
  anchor.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
</script>

<template>
  <header class="top">
    <div class="brand"><span class="mark" aria-hidden="true">≋</span> VoiceGuard</div>
    <nav aria-label="Secciones"><a href="#analisis">Analizador</a><a href="#historial">Historial</a><a href="#ayuda">Qué hacer</a></nav>
    <button class="status secondary" @click="checkStatus"><span :class="{ online: status === 'Modelo disponible' }"></span>{{ status }}</button>
  </header>
  <main>
    <section class="intro" id="analisis">
      <h1>Analiza una grabación sospechosa</h1>
      <p>Obtén una clasificación de voz y consulta tus análisis recientes.</p>
    </section>
    <div class="workspace">
      <section class="panel">
        <h2>Grabación</h2>
        <div class="dropzone" @dragover.prevent @drop.prevent="choose($event.dataTransfer.files[0])">
          <input ref="fileInput" type="file" accept=".wav,audio/wav" hidden @change="choose($event.target.files[0])">
          <button type="button" class="secondary" :disabled="busy" @click="fileInput.click()">Seleccionar WAV</button>
          <p>O arrastra el archivo aquí</p>
          <small>PCM de 16 bits · 1 segundo mínimo · hasta 1 MB</small>
        </div>
        <div v-if="selected" class="preview">
          <div><strong>{{ selected.name }}</strong><small>{{ Math.ceil(selected.size / 1024) }} KB</small></div>
          <button class="secondary" :disabled="busy" @click="resetSelection">Quitar</button>
          <audio controls :src="preview" aria-label="Escuchar grabación"></audio>
        </div>
        <button class="primary analyze" :disabled="!selected || busy" @click="analyze">{{ busy ? 'Analizando…' : 'Analizar audio' }}</button>
        <p class="error" v-if="error" role="alert">{{ error }}</p>
      </section>

      <section class="panel result" aria-live="polite">
        <h2>Diagnóstico</h2>
        <div v-if="busy" class="placeholder">Procesando la grabación…</div>
        <div v-else-if="result" class="diagnosis" :class="result.resultado">
          <p class="classification">{{ label(result.resultado) }}</p>
          <p><strong class="score">{{ percent(result.confianza) }}</strong> confianza del modelo</p>
          <meter min="0" max="1" :value="result.confianza" aria-label="Confianza del modelo"></meter>
          <p>La clasificación es orientativa. Verifica la identidad por un canal conocido si la solicitud es sospechosa.</p>
          <a v-if="result.resultado === 'sintetica'" href="#ayuda">Ver recomendaciones de seguridad</a>
        </div>
        <div v-else class="placeholder"><span aria-hidden="true">≋</span>El diagnóstico aparecerá aquí después del análisis.</div>
      </section>
    </div>

    <section class="panel" id="historial">
      <div class="section-head">
        <div><h2>Historial local</h2><p>Este navegador conserva hasta 100 resultados. Los audios no se guardan en el historial.</p></div>
        <button class="secondary" :disabled="!history.length" @click="exportHistory">Exportar JSON</button>
      </div>
      <div class="stats">
        <div><small>Análisis guardados</small><strong>{{ history.length }}</strong></div>
        <div><small>Posibles voces sintéticas</small><strong>{{ synthetic }}</strong></div>
        <div><small>Proporción señalada</small><strong>{{ flaggedRate }}%</strong></div>
      </div>
      <div class="controls">
        <label>Clasificación
          <select v-model="filter"><option value="todos">Todas</option><option value="real">Probable voz real</option><option value="sintetica">Posible voz sintética</option></select>
        </label>
        <button v-if="!confirmClear" class="secondary" :disabled="!history.length" @click="confirmClear = true">Borrar historial</button>
        <div v-else class="confirm"><span>¿Borrar todos los resultados locales?</span>
          <button class="secondary" @click="saveHistory([]); confirmClear = false">Confirmar borrado</button>
          <button class="secondary" @click="confirmClear = false">Cancelar</button>
        </div>
      </div>
      <p class="error" v-if="historyError" role="alert">{{ historyError }}</p>
      <p class="empty" v-if="!filtered.length">{{ history.length ? 'Sin resultados para este filtro.' : 'Todavía no hay análisis guardados.' }}</p>
      <div v-else class="table-wrap"><table>
        <thead><tr><th>Archivo</th><th>Fecha</th><th>Diagnóstico</th><th>Confianza</th></tr></thead>
        <tbody><tr v-for="row in filtered" :key="row.id"><td>{{ row.archivo }}</td><td>{{ date.format(new Date(row.fecha)) }}</td><td>{{ label(row.resultado) }}</td><td>{{ percent(row.confianza) }}</td></tr></tbody>
      </table></div>
    </section>

    <section class="panel" id="ayuda">
      <h2>Qué hacer ante una llamada sospechosa</h2>
      <ol><li>Cuelga y llama al número oficial o al contacto que ya tenías guardado.</li>
      <li>Evita compartir claves, códigos de verificación y datos bancarios.</li>
      <li>Confirma la solicitud por otro canal antes de transferir dinero.</li></ol>
    </section>
  </main>
</template>

<style>
:root{font-family:Inter,system-ui,sans-serif;color:#eaf1f5;background:#0e1822;color-scheme:dark;line-height:1.5;--muted:#a5b8c5;--border:#334553;--accent:#91eccd}
*{box-sizing:border-box}body{margin:0}a{color:var(--accent)}button,select{font:inherit}button{cursor:pointer}button:disabled{cursor:default;opacity:.45}button:focus-visible,a:focus-visible,select:focus-visible{outline:2px solid var(--accent);outline-offset:3px}
.top{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:18px max(24px,calc((100vw - 1100px)/2));border-bottom:1px solid var(--border);background:#13202c}.brand{display:flex;align-items:center;gap:10px;font-weight:750;font-size:1.25rem}.mark{background:var(--accent);color:#102923;padding:0 10px;border-radius:9px;font-size:1.5rem}.top nav{display:flex;gap:20px}.top a{color:#cbdbe4;text-decoration:none}.status{white-space:nowrap}.status span{display:inline-block;width:8px;height:8px;border-radius:50%;background:#e8b878;margin-right:7px}.status span.online{background:var(--accent)}
main{max-width:1148px;margin:auto;padding:34px 24px 52px;display:grid;gap:20px}h1,h2,p{margin:0}h1{font-size:clamp(1.7rem,4vw,2.65rem);line-height:1.15;letter-spacing:-.035em}h2{font-size:1.13rem;margin-bottom:10px}.intro p,.section-head p,.dropzone p,small,.empty{color:var(--muted)}.intro p{margin-top:10px}.workspace{display:grid;grid-template-columns:1fr 1fr;gap:20px}.panel{background:#162531;border:1px solid var(--border);border-radius:16px;padding:22px;min-width:0}.dropzone{border:1px dashed #597587;border-radius:12px;text-align:center;padding:22px 12px;background:#13202b}.dropzone p{margin-top:8px}.primary,.secondary,select{border-radius:8px;padding:8px 12px;border:1px solid var(--border);background:#203340;color:#eaf1f5}.primary{background:var(--accent);color:#142922;border-color:transparent;font-weight:700}.analyze{width:100%;margin-top:12px}.preview{margin-top:12px;display:grid;grid-template-columns:1fr auto;gap:8px;align-items:center;overflow-wrap:anywhere}.preview small{display:block}.preview audio{grid-column:1/-1;width:100%;height:36px}.error{margin-top:10px;color:#ffc2aa;background:#442c29;border-radius:8px;padding:9px}
.result{display:flex;flex-direction:column}.placeholder{min-height:175px;flex:1;display:grid;place-content:center;text-align:center;color:var(--muted);padding:20px}.placeholder span{font-size:2rem;color:#7eaaa4}.diagnosis{display:grid;gap:9px;border-radius:12px;padding:16px;background:#203935}.diagnosis.sintetica{background:#432d2b}.classification{color:var(--accent);font-size:1.18rem;font-weight:750}.sintetica .classification{color:#ffc2aa}.score{font-size:2rem;margin-right:5px}meter{width:100%;height:16px}
.section-head,.controls,.confirm{display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap}.stats{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin:14px 0}.stats div{display:grid;padding:12px;background:#13202b;border-radius:8px}.stats strong{font-size:1.4rem}.controls label{display:flex;align-items:center;gap:8px}.empty{padding:14px 0 0}.table-wrap{overflow:auto}table{border-collapse:collapse;width:100%;margin-top:10px;text-align:left}th,td{border-bottom:1px solid var(--border);padding:8px;overflow-wrap:anywhere}th{color:var(--muted)}ol{margin:8px 0 0;padding-left:24px}
@media(max-width:700px){.top{flex-wrap:wrap;padding:14px 16px}.top nav{order:3;width:100%}main{padding:26px 14px}.workspace{grid-template-columns:1fr}.panel{padding:16px}.stats div{padding:8px}}
</style>
