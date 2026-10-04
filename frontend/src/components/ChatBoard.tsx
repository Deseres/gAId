import { useState, useEffect, useRef } from 'react';
import type { Message } from '../types';

interface ChatBoardProps {
  messages: Message[];
  onSendMessage: (text: string) => void;
}

export default function ChatBoard({ messages, onSendMessage }: ChatBoardProps) {
  const [input, setInput] = useState('');
  const messagesEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    onSendMessage(input.trim());
    setInput('');
  };

  return (
    <div className="flex flex-col h-full bg-white relative rounded-t-[2rem]">
      <div className="flex-grow overflow-y-auto p-6 space-y-5 pb-24 hide-scrollbar">
        {messages.map((msg) => (
          <div key={msg.id} className={`flex ${msg.sender === 'user' ? 'justify-end' : 'justify-start'}`}>
            <div className={`max-w-[85%] px-5 py-3.5 text-[15px] font-medium leading-relaxed shadow-sm ${
              msg.sender === 'user' 
                ? 'bg-black text-white rounded-3xl rounded-br-md' 
                : 'bg-gray-100 text-gray-800 rounded-3xl rounded-bl-md'
            }`}>
              {msg.isTyping ? (
                <div className="flex gap-1.5 items-center h-5 px-1">
                  <span className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" />
                  <span className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '0.15s' }} />
                  <span className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '0.3s' }} />
                </div>
              ) : (
                msg.text
              )}
            </div>
          </div>
        ))}
        <div ref={messagesEndRef} />
      </div>

      <div className="absolute bottom-0 w-full p-4 bg-gradient-to-t from-white via-white to-transparent">
        <form onSubmit={handleSubmit} className="flex gap-2 relative">
          <input 
            type="text"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="What do you want to see?"
            className="flex-grow bg-gray-100/80 backdrop-blur-md rounded-full px-6 py-4 text-[15px] font-medium text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-black/5 border border-gray-200/50 shadow-sm transition-all"
          />
          <button 
            type="submit"
            className="bg-blue-600 text-white p-4 rounded-full shadow-md hover:bg-blue-700 active:scale-95 transition-all shrink-0 flex items-center justify-center"
          >
            <svg className="w-5 h-5 ml-1" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" d="M12 19l9 2-9-18-9 18 9-2zm0 0v-8"></path>
            </svg>
          </button>
        </form>
      </div>
    </div>
  );
}