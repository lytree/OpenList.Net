import { computed, defineComponent, ref, watch, type PropType } from "vue"
import { ElAlert, ElButton, ElDialog, ElMessage, ElProgress } from "element-plus"
import {
  canStreamSave,
  collectEntries,
  MAX_BLOB_BYTES,
  pickSaveTarget,
  saveAsBlob,
  writeZip,
  type ZipEntry,
} from "~/utils/zip"
import { baseName, pathBase } from "~/utils/path"
import { humanSize } from "~/utils/format"
import type { Obj } from "~/api/types"

/**
 * 打包下载（前端 zip）。
 *
 * 三段式：收集结构 → 选择落盘方式 → 边压边写。
 * 每段的进度分开显示，是因为它们失败的**原因和处置完全不同**：
 * 收集失败通常是某个子目录没权限，打包失败通常是网络中断。
 */
type Phase = "idle" | "collecting" | "zipping" | "done" | "error"

export default defineComponent({
  name: "PackageDownloadDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    targets: { type: Array as PropType<Obj[]>, default: () => [] },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
  },
  setup(props, { emit }) {
    const phase = ref<Phase>("idle")
    const message = ref("")
    const found = ref(0)
    const foundBytes = ref(0)
    const doneCount = ref(0)
    const totalCount = ref(0)
    const writtenBytes = ref(0)
    const totalBytes = ref(0)
    const controller = ref<AbortController | null>(null)

    const fileName = computed(() => {
      if (props.targets.length === 1) return `${props.targets[0].name}.zip`
      const folder = baseName(props.dir) || pathBase(props.dir) || "OpenList"
      return `${folder}.zip`
    })

    const percent = computed(() => {
      if (phase.value === "collecting") return 0
      if (totalBytes.value <= 0) return totalCount.value ? Math.round((doneCount.value / totalCount.value) * 100) : 0
      return Math.min(99, Math.round((writtenBytes.value / totalBytes.value) * 100))
    })

    watch(
      () => props.visible,
      (open) => {
        if (!open) {
          controller.value?.abort()
          controller.value = null
        } else {
          phase.value = "idle"
          message.value = ""
          found.value = 0
          foundBytes.value = 0
          doneCount.value = 0
          totalCount.value = 0
          writtenBytes.value = 0
          totalBytes.value = 0
        }
      },
    )

    function close() {
      controller.value?.abort()
      emit("update:visible", false)
    }

    async function run() {
      if (props.targets.length === 0) {
        ElMessage.warning("请先选择要打包的内容")
        return
      }
      const ac = new AbortController()
      controller.value = ac
      phase.value = "collecting"
      message.value = "正在统计目录结构…"

      try {
        const { entries, totalBytes: bytes } = await collectEntries(
          props.dir,
          props.targets,
          "",
          (n, b) => {
            found.value = n
            foundBytes.value = b
          },
          ac.signal,
        )
        totalBytes.value = bytes

        if (!canStreamSave() && bytes > MAX_BLOB_BYTES) {
          phase.value = "error"
          message.value = `待打包内容约 ${humanSize(bytes)}，超过浏览器内存上限 ${humanSize(
            MAX_BLOB_BYTES,
          )}。请分批选择子目录下载，或使用支持「另存为」的 Chromium 内核浏览器。`
          return
        }

        // 落盘方式先定下来：用户在文件选择器里取消时，不应该继续白耗流量。
        let sink: (chunk: Uint8Array, final: boolean) => Promise<void> | void
        let target: Awaited<ReturnType<typeof pickSaveTarget>> | null = null
        // 非流式回退时，chunk 先攒在这里，全部结束后一次性写成 Blob。
        let blobChunks: Uint8Array[] | null = null
        if (canStreamSave()) {
          try {
            target = await pickSaveTarget(fileName.value)
          } catch (err) {
            if ((err as Error)?.name === "AbortError") {
              phase.value = "idle"
              message.value = ""
              return
            }
            throw err
          }
          sink = async (chunk) => {
            await target!.write(chunk)
          }
        } else {
          const chunks: Uint8Array[] = []
          blobChunks = chunks
          sink = (chunk) => {
            chunks.push(chunk)
          }
        }

        phase.value = "zipping"
        message.value = "正在打包…"
        totalCount.value = entries.length

        await writeZip(
          entries,
          sink,
          (p) => {
            doneCount.value = p.done
            writtenBytes.value = p.bytes
            totalCount.value = p.total
          },
          ac.signal,
        )

        if (target) {
          await target.close()
        } else {
          saveAsBlob(blobChunks ?? [], fileName.value)
        }

        phase.value = "done"
        message.value = `已生成 ${fileName.value}（${humanSize(
          bytes,
        )}，${entries.filter((e: ZipEntry) => !e.isDir).length} 个文件）`
        ElMessage.success("打包完成")
      } catch (err) {
        if ((err as Error)?.name === "AbortError") {
          phase.value = "idle"
          message.value = ""
          return
        }
        phase.value = "error"
        message.value = (err as Error)?.message || String(err)
      } finally {
        controller.value = null
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title="打包下载"
        width="520px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
        v-slots={{
          default: () => (
            <div class="ol-dialog-col">
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">文件</span>
                <span class="ol-dialog-path">{fileName.value}</span>
              </div>
              <div class="ol-dialog-field">
                <span class="ol-dialog-label">内容</span>
                <span class="ol-dialog-hint">
                  {props.targets.length} 个选中项
                  {phase.value === "collecting"
                    ? ` · 已发现 ${found.value} 项（${humanSize(foundBytes.value)}）`
                    : totalCount.value
                      ? ` · 共 ${totalCount.value} 个条目（${humanSize(totalBytes.value)}）`
                      : ""}
                </span>
              </div>
              {(phase.value === "zipping" || phase.value === "done") && (
                <ElProgress percentage={percent.value} striped={phase.value === "zipping"} />
              )}
              {message.value && (
                <ElAlert
                  type={phase.value === "error" ? "error" : phase.value === "done" ? "success" : "info"}
                  showIcon={true}
                  closable={false}
                  title={message.value}
                />
              )}
              {phase.value === "idle" && (
                <span class="ol-dialog-hint">
                  {canStreamSave()
                    ? "将弹出保存位置选择框，边打包边写入磁盘，不占用内存。"
                    : `当前浏览器不支持流式保存，将在内存中打包后下载（上限 ${humanSize(
                        MAX_BLOB_BYTES,
                      )}）。`}
                </span>
              )}
            </div>
          ),
          footer: () => (
            <div class="ol-dialog-footer">
              <ElButton onClick={close}>
                {phase.value === "zipping" || phase.value === "collecting" ? "取消" : "关闭"}
              </ElButton>
              {phase.value === "idle" || phase.value === "error" ? (
                <ElButton type="primary" onClick={run}>
                  开始打包
                </ElButton>
              ) : null}
            </div>
          ),
        }}
      />
    )
  },
})
