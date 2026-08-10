namespace WE_Tool.Models
{
    /// <summary>combo 下拉选项：显示 label，写回 value</summary>
    public class ComboOption
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";

        /// <summary>ComboBox 默认以 ToString 显示项内容（WinUI 无 DisplayMemberPath）</summary>
        public override string ToString() => Label;
    }
}
