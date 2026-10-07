using System.Windows;
using System.Windows.Markup;

namespace BatteryCheck
{
    /// <summary>
    /// Полоса прокрутки в стиле окна вместо системной: без стрелок и фона дорожки, тонкий скруглённый ползунок цвета
    /// второстепенного текста; при наведении на полосу он шире и ярче, при перетаскивании — ещё ярче. Цвета — ресурсы темы
    /// (DynamicResource), поэтому смена светлой и тёмной темы перекрашивает и полосы. Стиль — на всё приложение.
    /// </summary>
    static class ScrollStyle
    {
        const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>

  <Style x:Key='BcScrollPage' TargetType='RepeatButton'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Border Background='Transparent'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='BcScrollThumb' TargetType='Thumb'>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='Bar' CornerRadius='3' Margin='3.5' Background='{DynamicResource Text2}' Opacity='0.35'/>
          <ControlTemplate.Triggers>
            <DataTrigger Binding='{Binding IsMouseOver, RelativeSource={RelativeSource AncestorType=ScrollBar}}' Value='True'>
              <Setter TargetName='Bar' Property='Margin' Value='2'/>
              <Setter TargetName='Bar' Property='Opacity' Value='0.55'/>
            </DataTrigger>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bar' Property='Opacity' Value='0.75'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='Bar' Property='Margin' Value='2'/>
              <Setter TargetName='Bar' Property='Opacity' Value='0.9'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <ControlTemplate x:Key='BcScrollVertical' TargetType='ScrollBar'>
    <Grid Background='Transparent'>
      <Track x:Name='PART_Track' IsDirectionReversed='True' Focusable='False'>
        <Track.DecreaseRepeatButton>
          <RepeatButton Style='{StaticResource BcScrollPage}' Command='ScrollBar.PageUpCommand'/>
        </Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton>
          <RepeatButton Style='{StaticResource BcScrollPage}' Command='ScrollBar.PageDownCommand'/>
        </Track.IncreaseRepeatButton>
        <Track.Thumb>
          <Thumb Style='{StaticResource BcScrollThumb}'/>
        </Track.Thumb>
      </Track>
    </Grid>
  </ControlTemplate>

  <ControlTemplate x:Key='BcScrollHorizontal' TargetType='ScrollBar'>
    <Grid Background='Transparent'>
      <Track x:Name='PART_Track' IsDirectionReversed='False' Focusable='False'>
        <Track.DecreaseRepeatButton>
          <RepeatButton Style='{StaticResource BcScrollPage}' Command='ScrollBar.PageLeftCommand'/>
        </Track.DecreaseRepeatButton>
        <Track.IncreaseRepeatButton>
          <RepeatButton Style='{StaticResource BcScrollPage}' Command='ScrollBar.PageRightCommand'/>
        </Track.IncreaseRepeatButton>
        <Track.Thumb>
          <Thumb Style='{StaticResource BcScrollThumb}'/>
        </Track.Thumb>
      </Track>
    </Grid>
  </ControlTemplate>

  <Style TargetType='ScrollBar'>
    <Setter Property='Stylus.IsFlicksEnabled' Value='False'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='12'/>
    <Setter Property='MinWidth' Value='12'/>
    <Setter Property='Template' Value='{StaticResource BcScrollVertical}'/>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width' Value='Auto'/>
        <Setter Property='MinWidth' Value='0'/>
        <Setter Property='Height' Value='12'/>
        <Setter Property='MinHeight' Value='12'/>
        <Setter Property='Template' Value='{StaticResource BcScrollHorizontal}'/>
      </Trigger>
    </Style.Triggers>
  </Style>
</ResourceDictionary>";

        public static void Install(ResourceDictionary app)
        {
            app.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
        }
    }
}
