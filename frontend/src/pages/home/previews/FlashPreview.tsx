import { defineComponent, onBeforeUnmount, onMounted, ref, type PropType } from "vue"
import { ElAlert } from "element-plus"
import { t } from "~/i18n"
import type { PreviewChildProps } from "./types"

/**
 * Flash（`.swf`）预览 —— 用 Ruffle（WASM 版 Flash 模拟器）在浏览器里跑。
 *
 * 为什么会话外还有一个「资源复制」步骤：`@ruffle-rs/ruffle` 是 **selfhosted** 包，
 * 运行时要从同目录加载 `core.ruffle.*.js` 与 ~14 MB 的 wasm。它用
 * `document.currentScript` 推断自身目录（默认 publicPath），因此：
 * 1. 只能在**运行时用 script 标签**加载 `/ruffle/ruffle.js`，交给打包器会被改写路径；
 * 2. 那几个文件由 `scripts/copy-ruffle.mjs` 从 node_modules 复制到 `public/ruffle/`
 *    （`dev`/`build` 前置执行），不进版本库 —— 28 MB 的二进制不该躺在 git 里。
 *
 * 拿不到资源时给出明确指引，而不是白屏：这是最容易在部署时漏掉的一步
 * （桌面端打包只带 dist，`public/ruffle` 必须在构建时复制进去）。
 */

/** 全局脚本只需加载一次；并发打开两个 swf 也共用同一个 Promise。 */
let rufflePromise: Promise<RuffleRoot> | null = null

interface RufflePlayerElement extends HTMLElement {
  src?: string
  /** Ruffle 官方 API：显式加载，比 `src` 属性可靠（见下面注释）。 */
  load?(url: string): Promise<unknown>
}

interface RuffleSource {
  createPlayer(): RufflePlayerElement
}

interface RuffleRoot {
  newest(): RuffleSource
  config: Record<string, unknown>
}

/**
 * 预览场景的 Ruffle 配置。
 *
 * `autoplay: "on"` 是**必须**的：默认值 `"auto"` 会遵守浏览器自动播放策略，
 * 无用户手势时影片停在第一帧并盖上「点击取消静音」遮罩 —— 表现为「加载完了但是黑屏」。
 * 用户是主动点进预览页的，这里直接强制播放。
 */
const RUFFLE_CONFIG: Record<string, unknown> = {
  autoplay: "on",
  unmuteOverlay: "hidden",
  letterbox: "on",
  scale: "showAll",
  contextMenu: "rightClickOnly",
}

function loadRuffle(): Promise<RuffleRoot> {
  if (rufflePromise) return rufflePromise
  rufflePromise = new Promise<RuffleRoot>((resolve, reject) => {
    const globals = window as unknown as { RufflePlayer?: RuffleRoot }
    if (globals.RufflePlayer?.newest) {
      resolve(globals.RufflePlayer)
      return
    }
    // 关键：配置必须在**脚本执行之前**挂到 window.RufflePlayer 上。
    // Ruffle 的初始化会把这个对象包成自己的实例（`new RufflePlayer(window.RufflePlayer)`），
    // 脚本跑完再赋值虽然改得到属性，但 `createPlayer()` 早就读过一遍默认值了
    // （实测症状：autoplay 没生效，影片停在「点击取消静音」遮罩后面）。
    globals.RufflePlayer = { ...(globals.RufflePlayer ?? {}), config: RUFFLE_CONFIG } as RuffleRoot

    const script = document.createElement("script")
    script.src = `${import.meta.env.BASE_URL}ruffle/ruffle.js`
    script.async = true
    script.onload = () => {
      const root = globals.RufflePlayer
      if (!root?.newest) {
        // 脚本 200 但没注册：多半是路径对、文件不对（被 SPA 的 index.html 兜底了）
        reject(new Error("RufflePlayer is not registered"))
        return
      }
      // 兜底：个别版本会重置 config，再合并一次（不覆盖运行时已写入的键）
      root.config = { ...RUFFLE_CONFIG, ...root.config }
      resolve(root)
    }
    script.onerror = () => reject(new Error(`Failed to load ${script.src}`))
    document.head.appendChild(script)
  }).catch((e) => {
    // 失败不缓存，下次进来还能重试（例如先关了静态资源服务再打开）
    rufflePromise = null
    throw e
  })
  return rufflePromise
}

export default defineComponent({
  name: "FlashPreview",
  props: {
    obj: { type: Object as PropType<PreviewChildProps["obj"]>, required: true },
    src: { type: String, required: true },
  },
  setup(props) {
    const host = ref<HTMLElement | null>(null)
    const error = ref("")
    const loading = ref(true)
    let player: HTMLElement | null = null

    onMounted(async () => {
      try {
        const ruffle = await loadRuffle()
        if (!host.value) return
        const instance = ruffle.newest().createPlayer()
        instance.style.width = "100%"
        instance.style.height = "100%"
        host.value.appendChild(instance)
        player = instance

        /*
         * 用 `load()` 而不是 `instance.src = url`：
         * `src` 在 selfhosted 版里是「反射属性」，只在元素已被 customElements 升级后
         * 才会落到 attribute 上；升级时序不稳时赋值会变成元素上的普通 JS 属性，
         * 既不触发加载也不报错 —— 现象是「播放器挂上了、永远是黑屏」。
         * `load()` 是元素上的正式方法，调用即触发。
         */
        if (typeof instance.load === "function") {
          await instance.load(props.src)
        } else {
          instance.src = props.src
        }
        loading.value = false
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
        loading.value = false
      }
    })

    onBeforeUnmount(() => {
      player?.remove()
      player = null
    })

    return () => (
      <div class="ol-flash-preview">
        {error.value ? (
          <div class="ol-office-body">
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              title={t("home.preview.flash_failed")}
              description={error.value}
            />
            <p class="ol-preview-hint">{t("home.preview.flash_hint")}</p>
          </div>
        ) : (
          <>
            {loading.value && (
              <div class="ol-flash-loading">{t("global.loading")}</div>
            )}
            <div ref={host} class="ol-flash-host" />
          </>
        )}
      </div>
    )
  },
})
