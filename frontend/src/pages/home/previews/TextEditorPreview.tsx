import { computed, defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert, ElButton, ElIcon, ElMessage, ElSkeleton, ElTag } from "element-plus"
import { Check } from "@element-plus/icons-vue"
import { Compartment, EditorState, type Extension } from "@codemirror/state"
import {
  EditorView,
  drawSelection,
  dropCursor,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
  rectangularSelection,
} from "@codemirror/view"
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands"
import {
  bracketMatching,
  defaultHighlightStyle,
  foldGutter,
  indentOnInput,
  syntaxHighlighting,
} from "@codemirror/language"
import { highlightSelectionMatches, searchKeymap } from "@codemirror/search"
import { fsPutFile } from "~/api/fs"
import { t } from "~/i18n"
import { ext } from "~/utils/path"
import type { PreviewChildProps } from "./types"

/** 超过这个体积不在浏览器里编辑：CodeMirror 撑得住，但「保存整份重写」不划算。 */
const SIZE_LIMIT = 5 * 1024 * 1024

/**
 * 文本编辑器（CodeMirror 6）。
 *
 * 为什么不是 Monaco（上游用的是它）：Monaco 要自己配 worker（ts/json/css/html 各一个），
 * 打包体积 ~800 KB gzip 起步；而这里需要的是「打开、改、存回去」，CodeMirror 6
 * 用 1/4 的体积给出同样的编辑体验（行号、折叠、多光标、搜索、撤销栈）。
 *
 * 两条硬约束：
 * 1. **只按 UTF-8 读写**。文件不是合法 UTF-8 时降级为只读并说明原因 ——
 *    强行用 UTF-8 保存会把 GBK 文件整份写坏，这比不能编辑糟得多。
 * 2. **保存是整份覆盖**（`PUT /fs/put` + `Overwrite`），没有版本号，
 *    所以有未保存改动时拦一手 `beforeunload`。
 */
export default defineComponent({
  name: "TextEditorPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    path: { type: String, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const loading = ref(true)
    const error = ref("")
    /** 只读原因（空串表示可编辑）。 */
    const readOnlyReason = ref("")
    const dirty = ref(false)
    const saving = ref(false)
    const position = ref({ line: 1, column: 1 })
    const chars = ref(0)
    const language = ref("plain")
    const host = ref<HTMLElement | null>(null)

    let view: EditorView | null = null
    /** 只读是运行时可变的：拿到 403 之后要把编辑器也冻住，而不只是禁用按钮。 */
    const readOnlyCompartment = new Compartment()
    /** 原始内容：判断 dirty 时与当前 doc 比较，撤销回原样就自动不算脏。 */
    let original = ""

    const tooLarge = computed(() => props.obj.size > SIZE_LIMIT)
    const canEdit = computed(() => !tooLarge.value && !readOnlyReason.value)

    /** 按扩展名懒加载语言包：每种语法各自一个 chunk，不打开就不下载。 */
    async function languageFor(name: string): Promise<{ name: string; extension: Extension | null }> {
      const extension = ext(name)
      try {
        if (extension === "json" || extension === "jsonc" || extension === "json5") {
          const { json } = await import("@codemirror/lang-json")
          return { name: "json", extension: json() }
        }
        if (["js", "mjs", "cjs", "jsx", "ts", "tsx"].includes(extension)) {
          const { javascript } = await import("@codemirror/lang-javascript")
          const typescript = ["ts", "tsx"].includes(extension)
          return {
            name: typescript ? "typescript" : "javascript",
            extension: javascript({ typescript, jsx: ["jsx", "tsx"].includes(extension) }),
          }
        }
        if (extension === "md" || extension === "markdown" || extension === "mdx") {
          const { markdown } = await import("@codemirror/lang-markdown")
          return { name: "markdown", extension: markdown() }
        }
        if (extension === "py") {
          const { python } = await import("@codemirror/lang-python")
          return { name: "python", extension: python() }
        }
      } catch {
        // 语言包加载失败只影响高亮，不该挡住编辑
      }
      return { name: extension || "plain", extension: null }
    }

    function buildExtensions(languageExtension: Extension | null): Extension[] {
      return [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightActiveLine(),
        foldGutter(),
        drawSelection(),
        dropCursor(),
        rectangularSelection(),
        history(),
        indentOnInput(),
        bracketMatching(),
        highlightSelectionMatches(),
        syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
        keymap.of([
          // Ctrl/Cmd+S 必须排在 defaultKeymap 之前，否则会被浏览器默认行为吞掉
          {
            key: "Mod-s",
            preventDefault: true,
            run: () => {
              void save()
              return true
            },
          },
          ...defaultKeymap,
          ...historyKeymap,
          ...searchKeymap,
          indentWithTab,
        ]),
        EditorView.lineWrapping,
        readOnlyCompartment.of(EditorState.readOnly.of(!canEdit.value)),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            const text = update.state.doc.toString()
            chars.value = text.length
            dirty.value = text !== original
          }
          if (update.selectionSet || update.docChanged) {
            const head = update.state.selection.main.head
            const line = update.state.doc.lineAt(head)
            position.value = { line: line.number, column: head - line.from + 1 }
          }
        }),
        ...(languageExtension ? [languageExtension] : []),
      ]
    }

    async function load() {
      loading.value = true
      error.value = ""
      readOnlyReason.value = ""
      dirty.value = false
      try {
        const response = await fetch(props.src)
        if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
        const bytes = new Uint8Array(await response.arrayBuffer())
        let text: string
        try {
          // fatal: 非法 UTF-8 直接抛错，而不是塞一堆 U+FFFD 然后被保存回磁盘
          text = new TextDecoder("utf-8", { fatal: true }).decode(bytes)
        } catch {
          text = new TextDecoder("utf-8").decode(bytes)
          readOnlyReason.value = t("home.preview.editor_encoding_unsupported")
        }
        original = text
        chars.value = text.length

        const detected = await languageFor(props.obj.name)
        language.value = detected.name

        if (host.value) {
          view?.destroy()
          view = new EditorView({
            state: EditorState.create({ doc: text, extensions: buildExtensions(detected.extension) }),
            parent: host.value,
          })
        }
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
      } finally {
        loading.value = false
      }
    }

    async function save() {
      if (!view || !canEdit.value || saving.value) return
      saving.value = true
      try {
        const body = new TextEncoder().encode(view.state.doc.toString())
        const resp = await fsPutFile(props.path, "", true, body)
        if (resp.code === 200) {
          original = view.state.doc.toString()
          dirty.value = false
          ElMessage.success(t("home.preview.editor_saved"))
        } else if (resp.code === 403) {
          // 只读目录/无写权限：把原因落到界面上，别只在 toast 里闪一下
          readOnlyReason.value = resp.message || t("home.preview.editor_readonly")
          view.dispatch({
            effects: readOnlyCompartment.reconfigure(EditorState.readOnly.of(true)),
          })
          ElMessage.error(readOnlyReason.value)
        } else {
          ElMessage.error(resp.message || `HTTP ${resp.code}`)
        }
      } catch (e) {
        ElMessage.error(String((e as Error)?.message ?? e))
      } finally {
        saving.value = false
      }
    }

    /** 有未保存改动时拦刷新/关标签页。 */
    function onBeforeUnload(event: BeforeUnloadEvent) {
      if (!dirty.value) return
      event.preventDefault()
      event.returnValue = ""
    }

    onMounted(() => {
      window.addEventListener("beforeunload", onBeforeUnload)
      if (tooLarge.value) {
        loading.value = false
        return
      }
      void load()
    })

    onBeforeUnmount(() => {
      window.removeEventListener("beforeunload", onBeforeUnload)
      view?.destroy()
      view = null
    })

    return () => (
      <div class="ol-editor-preview">
        <div class="ol-preview-toolbar ol-preview-toolbar-wrap">
          <div class="ol-preview-toolbar-right">
            <ElTag size="small" type="info">
              {language.value}
            </ElTag>
            <span class="ol-preview-hint">
              {t("home.preview.editor_position", {
                line: position.value.line,
                col: position.value.column,
              })}
            </span>
            <span class="ol-preview-hint">{t("home.preview.editor_chars", { count: chars.value })}</span>
            {dirty.value && (
              <ElTag size="small" type="warning">
                {t("home.preview.editor_dirty")}
              </ElTag>
            )}
            {!canEdit.value && (
              <ElTag size="small" type="info">
                {t("home.preview.editor_readonly")}
              </ElTag>
            )}
          </div>
          <div class="ol-preview-toolbar-right">
            <ElButton
              size="small"
              type="primary"
              disabled={!canEdit.value || !dirty.value}
              loading={saving.value}
              onClick={save}
            >
              <ElIcon style={{ marginRight: "4px" }}>
                <Check />
              </ElIcon>
              {t("home.preview.editor_save")}
            </ElButton>
          </div>
        </div>

        {tooLarge.value ? (
          <div class="ol-office-body">
            <ElAlert type="info" showIcon={true} closable={false} title={t("home.preview.editor_unsupported")} />
          </div>
        ) : error.value ? (
          <div class="ol-office-body">
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={t("home.preview.failed_to_render", { message: error.value })}
            />
          </div>
        ) : (
          <>
            {readOnlyReason.value && (
              <div class="ol-office-body">
                <ElAlert type="warning" showIcon={true} closable={false} title={readOnlyReason.value} />
              </div>
            )}
            {loading.value && (
              <div class="ol-office-body">
                <ElSkeleton rows={10} animated={true} />
              </div>
            )}
            <div ref={host} class="ol-editor-host" />
          </>
        )}
      </div>
    )
  },
})
