import { defineComponent, ref } from "vue"
import { ElAlert, ElButton, ElDialog, ElMessage } from "element-plus"
import { fsRemoveEmptyDirectory } from "~/api/fs"

/**
 * 删除当前目录下的空子目录（递归）。
 *
 * 只删**空**目录：后端 `RemoveEmptyDirectoryAsync` 自底向上清理，遇到非空目录就停。
 * 这是「整理下载目录」最常用的收尾操作，不会碰任何文件。
 */
export default defineComponent({
  name: "RemoveEmptyDirDialog",
  props: {
    visible: { type: Boolean, required: true },
    dir: { type: String, required: true },
  },
  emits: {
    "update:visible": (_v: boolean) => true,
    done: () => true,
  },
  setup(props, { emit }) {
    const busy = ref(false)

    function close() {
      emit("update:visible", false)
    }

    async function submit() {
      busy.value = true
      try {
        const resp = await fsRemoveEmptyDirectory(props.dir)
        if (resp.code !== 200) {
          ElMessage.error(resp.message || "操作失败")
          return
        }
        ElMessage.success("已清理空目录")
        emit("done")
        close()
      } finally {
        busy.value = false
      }
    }

    return () => (
      <ElDialog
        modelValue={props.visible}
        title="删除空目录"
        width="440px"
        onUpdate:modelValue={(v: boolean) => emit("update:visible", v)}
        v-slots={{
          default: () => (
            <div class="ol-dialog-col">
              <ElAlert
                type="info"
                showIcon={true}
                closable={false}
                title="将递归删除此目录下所有不含文件的子目录，文件本身不受影响。"
              />
              <span class="ol-dialog-path">{props.dir}</span>
            </div>
          ),
          footer: () => (
            <div class="ol-dialog-footer">
              <ElButton onClick={close}>取消</ElButton>
              <ElButton type="danger" loading={busy.value} onClick={submit}>
                开始清理
              </ElButton>
            </div>
          ),
        }}
      />
    )
  },
})
