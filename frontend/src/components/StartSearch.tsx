import { useEffect, useRef } from 'react';
import { useMapsLibrary } from '@vis.gl/react-google-maps';
import type { Place } from '../types';

export default function StartSearch({ onSelect }: { onSelect: (place: Place) => void }) {
  const places = useMapsLibrary('places');
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!places || !container.current) return;

    const input = new places.PlaceAutocompleteElement({});
    container.current.appendChild(input);

    const handleSelect = async (event: any) => {
      const place = event.placePrediction.toPlace();
      await place.fetchFields({ fields: ['id', 'displayName', 'location'] });
      
      if (place.location) {
        onSelect({
          name: place.displayName ?? '',
          google_place_id: place.id,
          lat: place.location.lat(),
          lng: place.location.lng(),
        });
      }
    };

    input.addEventListener('gmp-select', handleSelect);

    return () => {
      input.removeEventListener('gmp-select', handleSelect);
      input.remove();
    };
  }, [places, onSelect]);

return (
    <div ref={container} className="w-full bg-white rounded-xl shadow-sm border border-gray-200 [&>gmp-place-autocomplete]:w-full">
      <style>
        {`
          @media (min-width: 768px) {
            gmp-place-autocomplete {
              position: relative !important;
              display: block !important;
            }
            
            gmp-place-autocomplete::part(prediction-list) {
              position: absolute !important;
              top: auto !important; 
              bottom: 100% !important; 
              margin-bottom: 8px !important;
              left: 0 !important;
              right: 0 !important;
              
              box-shadow: 0 -10px 15px rgba(0, 0, 0, 0.05) !important;
              border-bottom-left-radius: 0 !important;
              border-bottom-right-radius: 0 !important;
              border-top-left-radius: 12px !important;
              border-top-right-radius: 12px !important;
            }
          }
          
          @media (max-width: 767px) {
            gmp-place-autocomplete::part(prediction-list) {
              margin-bottom: 24px !important;
            }
          }
        `}
      </style>
    </div>
  );
}