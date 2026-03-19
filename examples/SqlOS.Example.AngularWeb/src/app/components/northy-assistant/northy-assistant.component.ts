import { Component, input } from '@angular/core';

@Component({
  selector: 'app-northy-assistant',
  standalone: true,
  template: `
    <div class="northy">
      <div class="northy-character">
        <div class="northy-bag">
          <div class="northy-face">{{ face() }}</div>
        </div>
      </div>
      <div class="northy-bubble">
        <p>{{ message() }}</p>
      </div>
    </div>
  `,
})
export class NorthyAssistantComponent {
  message = input.required<string>();
  mood = input.required<'happy' | 'alert' | 'wave' | 'thinking'>();

  face = () => {
    switch (this.mood()) {
      case 'alert': return '😯';
      case 'wave': return '👋';
      case 'thinking': return '🤔';
      default: return '😊';
    }
  };
}
