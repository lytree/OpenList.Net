import { computed, defineComponent, ref, watch, type PropType } from "vue"
import {
  ElAlert,
  ElButton,
  ElCheckbox,
  ElDialog,
  ElInput,
  ElMessage,
} from "element-plus"
import { fsArchiveDecompress } from "~/api/fs"
import FolderPickerDialog from "~/components/FolderPickerDialog"
import { ext, pathJoin } from "~/utils/path"
import { ARCHIVE_EXTS, isArchiveName } from "~/utils/archive"
import type { Obj } from "~/api/types"

/**
 * 解压归档文件。
 *
 * 后端只负责「把这个归档解到那个目录」：`src_dir` + `name[]` 拼出归档路径，
 * `put_into_new_dir` 决定是否再套一层同名目录。可以在下一步接 `archive/list`
 * 做「只解其中几个文件」，当前先不做（选择内部条目的 UI 成本高于收益）。
 *
 * 支持的扩展名以 `GET /api/fs/archive/meta` 为准（后端返回 .zip/.tar/.gz/.rar/.7z），
 * 这里按同一份列表在前端先拦一道，避免用户对着 mp4 点解压。
 */
// 归档类型判断统一放在 utils/archive（预览器与解压对话框共用同一张表）

export default defineComponent({
  name: "DecompressDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    target: { type: Object as PropType<Obj | null>, default: null },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const dstDir = ref("")
    const password = ref("")
    const intoNewDir = ref(true)
    const overwrite = ref(false)
    const busy = ref(false)
    const pickerVisible = ref(false)

    const archivePath = computed(() =>
      props.target ? pathJoin(props.dir, props.target.name) : "",
    )

    /** 扩展名判定要处理 `.tar.gz` 这种两段后缀。 */
    const supported = computed(() => isArchiveName(props.target?.name ?? ""))

    watch(
      () => props.visible,
      (open) => {
        if (open) {
          // 默认解到当前目录：这是「就地解压」的常见预期，选别的目录再点选择。
          dstDir.value = props.dir
          password.value = ""
          intoNewDir.value = true
          overwrite.value = false
        }
      },
    )

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      if (!props.target) return
      if (!supported.value) {
        ElMessage.warning(`不支持解压 .${ext(props.target.name)} 文件`)
        return
      }
      busy.value = true
      try {
        const resp = await fsArchiveDecompress({
          src_dir: props.dir,
          dst_dir: dstDir.value || props.dir,
          name: [props.target.name],
          archive_pass: password.value || undefined,
          inner_path: "/",
          cache_full: true,
          put_into_new_dir: intoNewDir.value,
          overwrite: overwrite.value,
        })
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "解压失败")
          return
        }
        ElMessage.success("已提交解压")
        emit("done")
        close()
      } finally {
        busy.value = false
      }
    }

    return () => (
      <>
        <ElDialog
          modelValue={props.visible}
          title="解压"
          width="520px"
          onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
          v-slots={{
            default: () => (
              <div class="ol-dialog-col">
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">归档文件</span>
                  <span class="ol-dialog-path">{archivePath.value || "-"}</span>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">解压到</span>
                  <div class="ol-dialog-row">
                    <span class="ol-dialog-path">{dstDir.value || "未选择"}</span>
                    <ElButton size="small" onClick={() => (pickerVisible.value = true)}>
                      选择
                    </ElButton>
                  </div>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">归档密码</span>
                  <ElInput
                    modelValue={password.value}
                    size="small"
                    type="password"
                    showPassword={true}
                    placeholder="可选，加密归档才需要"
                    style={{ maxWidth: "260px" }}
                    onUpdate:modelValue={(v: string) => (password.value = v)}
                  />
                </div>
                <div class="ol-dialog-col" style={{ gap: "6px" }}>
                  <ElCheckbox
                    modelValue={intoNewDir.value}
                    onUpdate:modelValue={(v: string | number | boolean) =>
                      (intoNewDir.value = Boolean(v))
                    }
                  >
                    解压到同名新目录（避免文件散落在当前目录）
                  </ElCheckbox>
                  <ElCheckbox
                    modelValue={overwrite.value}
                    onUpdate:modelValue={(v: string | number | boolean) =>
                      (overwrite.value = Boolean(v))
                    }
                  >
                    覆盖同名文件
                  </ElCheckbox>
                </div>
                {!supported.value && (
                  <ElAlert
                    type="error"
                    showIcon={true}
                    closable={false}
                    title={`不支持解压该类型（支持：${ARCHIVE_EXTS.join(" / ")}）`}
                  />
                )}
              </div>
            ),
            footer: () => (
              <div class="ol-dialog-footer">
                <ElButton onClick={close}>取消</ElButton>
                <ElButton
                  type="primary"
                  loading={busy.value}
                  disabled={!supported.value}
                  onClick={submit}
                >
                  开始解压
                </ElButton>
              </div>
            ),
          }}
        />
        <FolderPickerDialog
          visible={pickerVisible.value}
          title="选择解压目标目录"
          initialDir={props.dir}
          onUpdate:visible={(v: boolean) => (pickerVisible.value = v)}
          onChosen={(dir: string) => {
            dstDir.value = dir
            pickerVisible.value = false
          }}
        />
      </>
    )
  },
})
