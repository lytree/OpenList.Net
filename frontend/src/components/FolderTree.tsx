import { computed, defineComponent, ref } from "vue"
import { ElTree } from "element-plus"
import { fsList } from "~/api/fs"
import { pathJoin } from "~/utils/path"

/**
 * 目录树（首页左侧导航）。
 *
 * 与旧前端 `components/EnhancedFolderTree` 的取舍：
 * - **懒加载**（`lazy` + `load`）：进入一个挂载点就预拉整棵树会让 S3/网盘类驱动
 *   打出几十次 List 请求；Element Plus 的懒加载天然只在展开时请求一层。
 * - **只用 `fs/list` + `is_dir` 过滤**，不用 `fs/dirs`（后者一次返回深度 3 的全路径、
 *   且对根目录有 `force_root` 的管理员语义，做增量展开反而别扭）。
 * - **点击节点即导航**（`expand-on-click-node=false`），箭头单独控制展开，
 *   避免「想展开却跳走」「想跳走却只是展开」两种误操作。
 *
 * 深链定位（`?path=/a/b/c`）不手工遍历节点：把当前路径的祖先链交给
 * `default-expanded-keys` / `current-node-key` 两个 prop。
 * Element Plus 的 `Node.setData` 在**节点插入时**会检查 defaultExpandedKeys，
 * 因此懒加载出来的子节点也会自动展开——这比自己 `expand()` 再轮询 `node.loaded`
 * 稳得多（后者的时序取决于加载回调何时 resolve，正是 ESLint 也管不了的竞态）。
 */

interface TreeNode {
  label: string
  path: string
  isLeaf?: boolean
}

/** 当前路径的祖先链（含自身），根恒为 "/"。 */
function ancestorKeys(path: string): string[] {
  const keys = ["/"]
  let cur = ""
  for (const seg of path.split("/").filter(Boolean)) {
    cur = `${cur}/${seg}`
    keys.push(cur)
  }
  return keys
}

export default defineComponent({
  name: "FolderTree",
  props: {
    current: { type: String, default: "/" },
    password: { type: String, default: "" },
  },
  emits: {
    navigate: (_path: string) => true,
  },
  setup(props, { emit }) {
    // data 的第一项会被 ElTree 当作（不渲染的）根节点，所以这里的 label 只是占位。
    const data = ref<TreeNode[]>([{ label: "/", path: "/" }])
    const loadError = ref("")

    const expandedKeys = computed(() => ancestorKeys(props.current))

    async function loadNode(node: any, resolve: (nodes: TreeNode[]) => void) {
      const parent: string = node?.data?.path ?? "/"
      const resp = await fsList(parent, props.password, 1, 0)
      if (resp.code !== 200) {
        loadError.value = resp.message
        resolve([])
        return
      }
      loadError.value = ""
      const dirs = (resp.data.content ?? [])
        .filter((obj) => obj.is_dir)
        .sort((a, b) => a.name.localeCompare(b.name, "zh-CN"))
        .map<TreeNode>((obj) => ({ label: obj.name, path: pathJoin(parent, obj.name) }))
      resolve(dirs)
    }

    return () => (
      <div class="ol-tree">
        <ElTree
          data={data.value}
          nodeKey="path"
          lazy={true}
          load={loadNode}
          props={{ label: "label", children: "children", isLeaf: "isLeaf" }}
          highlightCurrent={true}
          expandOnClickNode={false}
          defaultExpandedKeys={expandedKeys.value}
          currentNodeKey={props.current}
          emptyText="暂无子目录"
          onNode-click={(node: TreeNode) => emit("navigate", node.path)}
        />
        {loadError.value && <p class="ol-tree-error">{loadError.value}</p>}
      </div>
    )
  },
})
