import { defineComponent, ref, watch } from "vue"
import { ElButton, ElCheckbox, ElDialog, ElInput, ElMessage } from "element-plus"
import { fsPutFile } from "~/api/fs"
import { pathJoin } from "~/utils/path"

/**
 * 新建空文件。
 *
 * 走 `PUT /fs/put`（与上传同一条通道），文件名为空体 + `File-Path` 头。
 * 这个接口本来就是「流式写文件」，零字节写即创建空文件；因此不需要后端加端点。
 *
 * 权限：`/fs/put` 要求 `CanWriteContent` + meta 写入校验，只读目录会 403，
 * 与「新建文件夹」的报错口径一致。
 */
export default defineComponent({
  name: "NewFileDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
    password: { type: String, default: "" },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    saved: () => true,
  },
  setup(props, { emit }) {
    const name = ref("")
    const overwrite = ref(false)
    const saving = ref(false)

    // 每次打开都重置：上一次输入的文件名留在这里只会让人误创建。
    watch(
      () => props.visible,
      (open) => {
        if (open) {
          name.value = ""
          overwrite.value = false
        }
      },
    )

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      const trimmed = name.value.trim()
      if (!trimmed) {
        ElMessage.warning("请输入文件名")
        return
      }
      if (trimmed.includes("/") || trimmed.includes("\\")) {
        ElMessage.warning("文件名不能包含路径分隔符")
        return
      }
      saving.value = true
      try {
        const resp = await fsPutFile(
          pathJoin(props.dir, trimmed),
          props.password,
          overwrite.value,
        )
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "创建失败")
          return
        }
        ElMessage.success("已创建")
        emit("saved")
        close()
      } finally {
        saving.value = false
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title="新建文件"
        width="420px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
        v-slots={{
          default: () => (
            <div
              class="ol-dialog-col"
              onKeydown={(e: KeyboardEvent) => {
                if (e.key === "Enter") void submit()
              }}
            >
              <ElInput
                modelValue={name.value}
                placeholder="文件名（含扩展名，如 notes.md）"
                onUpdate:modelValue={(v: string) => (name.value = v)}
              />
              <ElCheckbox
                modelValue={overwrite.value}
                onUpdate:modelValue={(v: string | number | boolean) =>
                  (overwrite.value = Boolean(v))
                }
              >
                覆盖同名文件
              </ElCheckbox>
            </div>
          ),
          footer: () => (
            <div class="ol-dialog-footer">
              <ElButton onClick={close}>取消</ElButton>
              <ElButton type="primary" loading={saving.value} onClick={submit}>
                创建
              </ElButton>
            </div>
          ),
        }}
      />
    )
  },
})
