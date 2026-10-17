using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeliveryApp.API.Models;

/// <summary>
/// مجموعة اختيارات على المنتج (مثلاً "اختار 2 صوص من 8"). مستقلة عن الـ Variants (المقاسات):
/// المنتج الواحد ممكن يبقى له مقاسات بأسعارها + مجموعة أو أكتر من الاختيارات.
/// MinSelect = 0 → اختيارية، >= 1 → إجبارية. MaxSelect = أقصى عدد اختيارات.
/// </summary>
public class ProductOptionGroup
{
    [Key]
    public int Id { get; set; }

    public int ProductId { get; set; }

    [StringLength(150)]
    public string Name { get; set; } = null!;

    public int MinSelect { get; set; }

    public int MaxSelect { get; set; } = 1;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    [ForeignKey(nameof(ProductId))]
    public virtual Product Product { get; set; } = null!;

    public virtual ICollection<ProductOptionItem> Items { get; set; } = new List<ProductOptionItem>();
}

/// <summary>اختيار واحد جوه المجموعة (مثلاً "ثوم"). ExtraPrice = زيادة السعر (الافتراضي 0 = من غير زيادة).</summary>
public class ProductOptionItem
{
    [Key]
    public int Id { get; set; }

    public int GroupId { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = null!;

    [Column(TypeName = "decimal(10, 2)")]
    public decimal ExtraPrice { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    [ForeignKey(nameof(GroupId))]
    public virtual ProductOptionGroup Group { get; set; } = null!;
}
